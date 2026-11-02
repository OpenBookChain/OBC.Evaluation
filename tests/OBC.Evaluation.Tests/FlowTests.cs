using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OBC.Evaluation.Core;
using OBC.Evaluation.Web.Data;
using OBC.Evaluation.Web.Services;

namespace OBC.Evaluation.Tests;

public sealed class AppFactory : WebApplicationFactory<Program>
{
    private readonly string dbPath = Path.Combine(Path.GetTempPath(), "obc-eval-tests", $"{Guid.NewGuid():N}.db");
    private readonly string agentsPath;

    /// <summary>Answers for the fake agent: given the work folder, the reply it "produces".</summary>
    public Func<string, AgentResult> Agent { get; set; } = _ => throw new InvalidOperationException("No fake agent set.");

    public AppFactory()
    {
        agentsPath = Path.ChangeExtension(dbPath, ".agents.json");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        File.WriteAllText(agentsPath, """{"agents": {"Test app": {"obc": {"command": "fake", "output": "text"}}}}""");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Evaluation:DatabasePath", dbPath);
        builder.UseSetting("Evaluation:AgentsConfig", agentsPath);
        builder.UseEnvironment("Development");
        builder.ConfigureServices(s => s.AddSingleton<IAgentLauncher>(new FakeLauncher(this)));
    }

    private sealed class FakeLauncher(AppFactory app) : IAgentLauncher
    {
        public Task<AgentResult> RunAsync(AgentCommand agent, string prompt, string workDir, string promptFile, TimeSpan timeout, CancellationToken cancel, Action<string>? onLine = null) =>
            Task.FromResult(app.Agent(workDir));
    }

    public async Task<AgentStatus> RunSessionAsync(AgentRunRequest request)
    {
        var runner = Services.GetRequiredService<AgentRunService>();
        Assert.Null(await runner.StartAsync(request));
        await runner.Completion;
        return runner.Status;
    }

    public async Task<T> WithEval<T>(Func<EvalService, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<EvalService>());
    }

    public Task WithEval(Func<EvalService, Task> action) => WithEval(async e => { await action(e); return 0; });
}

public class FlowTests : IClassFixture<AppFactory>
{
    internal const string QaReply ="""{"answer_status":"answered","answer_type":"number","final_answer":1.5,"unit":"GBP","method":"Summed.","assumptions":[],"data_warnings":[],"source_references":[],"confidence":"high"}""";
    internal const string PreparerReply ="<<<REPORT>>>\nIncome £1.50 [C1].\n<<<END REPORT>>>\n<<<WORKING PAPERS>>>\nWP-C1\n<<<END WORKING PAPERS>>>\n<<<CLAIMS JSON>>>\n{\"claims\":[]}\n<<<END CLAIMS JSON>>>";

    private readonly AppFactory app;

    public FlowTests(AppFactory app) => this.app = app;

    private Task<ModelProfile> AddModel(string mode) => app.WithEval(e => e.AddModelAsync(new ModelProfile
    {
        Name = $"Test {Guid.NewGuid():N}"[..13], Provider = "Test", ModelVersion = "test-1", App = "Test app", Mode = mode,
    }));

    private Task TickAsync(int size, params string[] steps) =>
        app.WithEval(async e => { foreach (var s in steps) await e.SetStepAsync($"{s}@{size}", true, "test"); });

    [Fact]
    public async Task Manual_runs_follow_the_checklist_and_audits_wait_for_their_preparer()
    {
        var model = await AddModel(Modes.Manual);
        await app.WithEval(async e =>
        {
            Assert.Equal(145, (await e.ViewsAsync(model.Id)).Count);

            await e.SetStepAsync("T1@5000", false, null);
            Assert.Null(await e.NextAsync(model.Id, 5000));
        });

        await TickAsync(5000, "T1", "T2", "T3");
        await app.WithEval(async e =>
        {
            var next = (await e.NextAsync(model.Id, 5000, "plain"))!;
            Assert.Equal(("B1.1@5000", "plain"), (next.Task.Id, next.Run.Setup));

            var attempt = await e.StartAsync(next.Run.Id, Modes.Manual);
            Assert.Equal(Hashing.Sha256(attempt.Prompt), attempt.PromptSha256);
            Assert.Equal("financial-data-answering", attempt.SkillName);
            Assert.Equal(attempt.Id, (await e.StartAsync(next.Run.Id, Modes.Manual)).Id);

            var check = await e.SubmitAsync(attempt.Id, QaReply);
            Assert.Equal(ReplyCheck.Valid, check.Status);
            Assert.Equal(RunState.Done, (await e.ViewAsync(next.Run.Id)).State);

            var views = await e.ViewsAsync(model.Id);
            var audit = views.First(v => v.Task.Id == "C1-audit@5000" && v.Run.Setup == "obc");
            Assert.Equal(RunState.Blocked, audit.State);
            Assert.StartsWith("Waiting for C1-prepare@5000", audit.BlockedBy);

            var prepare = views.First(v => v.Task.Id == "C1-prepare@5000" && v.Run.Setup == "obc");
            var prep = await e.StartAsync(prepare.Run.Id, Modes.Manual);
            await e.SubmitAsync(prep.Id, PreparerReply);

            var auditAttempt = await e.StartAsync(audit.Run.Id, Modes.Manual);
            Assert.Contains("<<<REPORT>>>\nIncome £1.50 [C1].\n<<<END REPORT>>>", auditAttempt.Prompt);
            Assert.Equal(prep.Id, auditAttempt.SourceAttemptId);
            Assert.Equal("audit-obc", auditAttempt.SkillName);

            var later = views.First(v => v.Task.Id == "D1.1@5000" && v.Run.Setup == "obc");
            Assert.Equal(RunState.Blocked, later.State);
            Assert.Contains("T5, T6", later.BlockedBy);

            var planted = views.First(v => v.Task.Kind == TaskKinds.PlantedAudit && v.Run.Setup == "obc");
            Assert.StartsWith("Waiting for C2-prepare@5000", (await e.ViewAsync(planted.Run.Id)).BlockedBy);
            var c2 = views.First(v => v.Task.Id == "C2-prepare@5000" && v.Run.Setup == "obc");
            var c2Attempt = await e.StartAsync(c2.Run.Id, Modes.Manual);
            await e.SubmitAsync(c2Attempt.Id, PreparerReply);
            Assert.Equal("Plant the errors in a copy of the preparer's output first", (await e.ViewAsync(planted.Run.Id)).BlockedBy);
            await e.SavePlantedAsync(planted.Run.Id, PromptBuilder.AuditSource(PreparerReply).Replace("1.50", "1.60"), c2Attempt.Id, "C1 off by 0.10");
            var plantedAttempt = await e.StartAsync(planted.Run.Id, Modes.Manual);
            Assert.Contains("£1.60", plantedAttempt.Prompt);
        });
    }

    [Fact]
    public async Task A_retry_adds_an_attempt_and_the_latest_submitted_one_counts()
    {
        var model = await AddModel(Modes.Manual);
        await TickAsync(500, "T1", "T2", "T3");
        await app.WithEval(async e =>
        {
            var next = (await e.NextAsync(model.Id, 500, "csv"))!;
            var first = await e.StartAsync(next.Run.Id, Modes.Manual);
            Assert.Equal(ReplyCheck.Invalid, (await e.SubmitAsync(first.Id, "pasted the wrong thing")).Status);
            await Assert.ThrowsAsync<InvalidOperationException>(() => e.StartAsync(next.Run.Id, Modes.Manual));

            var second = await e.StartAsync(next.Run.Id, Modes.Manual, retry: true);
            Assert.Equal(2, second.Number);
            Assert.EndsWith("Data file: base.csv\n", second.Prompt);
            await e.SubmitAsync(second.Id, QaReply);
            Assert.Equal(ReplyCheck.Valid, (await e.ViewAsync(next.Run.Id)).Latest!.Validation);
        });
    }

    private static AgentResult Ok(AgentContext? context = null) =>
        new(true, QaReply, null, 42.5, new AgentUsage(100, 20, 300, 40, 0.01m), 3, "fake-agent", "{}", context, 2);

    [Fact]
    public async Task An_agent_session_runs_ready_runs_in_fresh_folders_and_saves_their_measures()
    {
        var agent = await AddModel(Modes.Agent);
        var folders = new List<string>();
        app.Agent = workDir =>
        {
            Assert.Empty(Directory.GetFileSystemEntries(workDir));
            folders.Add(workDir);
            return Ok(new AgentContext("test-1", [], ["obc"], null));
        };

        await TickAsync(500, "T1", "T2", "T3");
        var status = await app.RunSessionAsync(new AgentRunRequest(agent.Id, 500, "obc", 2, null));

        Assert.Equal((AgentServiceState.Idle, 2, 0, 0.02m), (status.State, status.Done, status.Failed, status.CostUsd));
        Assert.Equal(2, folders.Distinct().Count());
        await app.WithEval(async e =>
        {
            var views = (await e.ViewsAsync(agent.Id)).Where(v => v.Latest?.Status == AttemptStatus.Submitted).ToList();
            Assert.Equal(2, views.Count);
            var saved = views[0].Latest!;
            Assert.Equal((Modes.Agent, 42.5, 100L, 0.01m, 3, 2), (saved.Mode, saved.DurationSeconds!.Value, saved.InputTokens!.Value, saved.CostUsd!.Value, saved.NumTurns!.Value, saved.ToolCalls!.Value));
            Assert.Equal(ReplyCheck.Valid, saved.Validation);
            Assert.Contains("Model reported: test-1.", saved.Notes);
            Assert.Equal(Hashing.Sha256(saved.Prompt), saved.PromptSha256);
        });
    }

    [Fact]
    public async Task A_run_whose_agent_loaded_a_skill_is_thrown_away_and_the_session_stops()
    {
        var agent = await AddModel(Modes.Agent);
        await TickAsync(500, "T1", "T2", "T3");
        app.Agent = _ => Ok(new AgentContext("test-1", ["caveman"], [], null));

        var status = await app.RunSessionAsync(new AgentRunRequest(agent.Id, 500, "obc", 5, null));

        Assert.Equal((0, 1), (status.Done, status.Failed));
        await app.WithEval(async e =>
            Assert.Contains((await e.ViewsAsync(agent.Id)).Select(v => v.Latest?.AbandonReason), r => r != null && r.Contains("caveman")));
    }

    [Fact]
    public async Task A_session_stops_after_three_failures_in_a_row_and_abandons_each_attempt()
    {
        var agent = await AddModel(Modes.Agent);
        await TickAsync(500, "T1", "T2", "T3");
        app.Agent = _ => new AgentResult(false, null, "not signed in", 1, null, null, "fake-agent", "raw");

        var status = await app.RunSessionAsync(new AgentRunRequest(agent.Id, 500, "obc", 0, null));

        Assert.Equal((0, AgentRunService.MaxFailuresInARow), (status.Done, status.Failed));
        // A failed run is offered again, so the same run holds all the abandoned attempts
        await app.WithEval(async e =>
        {
            var run = (await e.ViewsAsync(agent.Id)).Single(v => v.Latest?.AbandonReason == "not signed in");
            var attempts = (await e.ViewAsync(run.Run.Id)).Run.Attempts;
            Assert.Equal(AgentRunService.MaxFailuresInARow, attempts.Count(a => a.Status == AttemptStatus.Abandoned && a.AbandonReason == "not signed in"));
        });
    }

    [Fact]
    public void One_command_serves_every_model_of_a_tool_through_the_model_placeholder()
    {
        var template = new AgentCommand { Command = "claude", Args = ["-p", "--model", "{model}"] };
        var command = AgentRunService.ForModel(template, new ModelProfile { ModelVersion = "claude-sonnet-5-5" });
        Assert.Equal(["-p", "--model", "claude-sonnet-5-5"], command.Args);
    }

    [Fact]
    public async Task A_manual_model_cannot_start_an_agent_session()
    {
        var manual = await AddModel(Modes.Manual);
        var runner = app.Services.GetRequiredService<AgentRunService>();
        Assert.NotNull(await runner.StartAsync(new AgentRunRequest(manual.Id, null, null, 1, null)));
    }

    [Fact]
    public async Task The_export_lists_every_file_with_its_hash_and_leaves_the_answer_key_out()
    {
        var model = await AddModel(Modes.Manual);
        await TickAsync(500, "T1", "T2", "T3");
        await app.WithEval(async e =>
        {
            var next = (await e.NextAsync(model.Id, 500))!;
            await e.SubmitAsync((await e.StartAsync(next.Run.Id, Modes.Manual)).Id, QaReply);
        });

        var bytes = await app.CreateClient().GetByteArrayAsync("export.zip");
        using var zip = new ZipArchive(new MemoryStream(bytes));
        var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open()).RootElement;
        var files = manifest.GetProperty("files").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());

        Assert.DoesNotContain(files.Keys, k => k.Contains("answer-key"));
        Assert.Contains("bundle/bundle.json", files.Keys);
        foreach (var entry in zip.Entries.Where(e => e.FullName != "manifest.json"))
        {
            using var stream = entry.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            Assert.Equal(files[entry.FullName], Hashing.Sha256(copy.ToArray()));
        }
        Assert.Contains(files.Keys, k => k.EndsWith("/prompt.md"));
        Assert.Contains(files.Keys, k => k.EndsWith("/reply.md"));
    }

    [Fact]
    public async Task Data_files_are_served_only_when_the_bundle_names_them()
    {
        var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("data/500/base.csv")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("data/500/answers.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("data/500/expected.json")).StatusCode);
    }
}
