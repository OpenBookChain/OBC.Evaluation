using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
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
    public const string ApiKey = "test-key";
    private readonly string dbPath = Path.Combine(Path.GetTempPath(), "obc-eval-tests", $"{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Evaluation:DatabasePath", dbPath);
        builder.UseSetting("Evaluation:AgentApiKey", ApiKey);
        builder.UseEnvironment("Development");
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
    private const string QaReply = """{"answer_status":"answered","answer_type":"number","final_answer":1.5,"unit":"GBP","method":"Summed.","assumptions":[],"data_warnings":[],"source_references":[],"confidence":"high"}""";
    private const string PreparerReply = "<<<REPORT>>>\nIncome £1.50 [C1].\n<<<END REPORT>>>\n<<<WORKING PAPERS>>>\nWP-C1\n<<<END WORKING PAPERS>>>\n<<<CLAIMS JSON>>>\n{\"claims\":[]}\n<<<END CLAIMS JSON>>>";

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

    [Fact]
    public async Task The_agent_api_hands_out_runs_and_takes_back_replies_with_their_measures()
    {
        var client = app.CreateClient();
        var agent = await AddModel(Modes.Agent);
        var manual = await AddModel(Modes.Manual);

        var noKey = await client.PostAsync($"api/agent/next?model={agent.Id}", null);
        Assert.True(noKey.StatusCode == HttpStatusCode.Unauthorized, await noKey.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add(AgentApi.KeyHeader, AppFactory.ApiKey);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"api/agent/next?model={manual.Id}", null)).StatusCode);

        await TickAsync(500, "T1", "T2", "T3");
        var response = await client.PostAsync($"api/agent/next?model={agent.Id}&size=500&setup=csv", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = (await response.Content.ReadFromJsonAsync<AgentRun>())!;
        Assert.Equal("csv", run.Setup);
        Assert.Equal(new AgentDataFile("retailer-500", "base.csv", "3b916f8b3a2b04c205c10b9b4dd587f6b1934fdadfed1b43ef3991d57135982d"), run.DataFile);

        // The run is taken: the next call gets the next one
        var other = (await (await client.PostAsync($"api/agent/next?model={agent.Id}&size=500&setup=csv", null)).Content.ReadFromJsonAsync<AgentRun>())!;
        Assert.NotEqual(run.RunId, other.RunId);
        await client.PostAsJsonAsync($"api/agent/attempts/{other.AttemptId}/abandon", new AgentAbandon("test", "raw"));

        var submit = await client.PostAsJsonAsync($"api/agent/attempts/{run.AttemptId}/submit",
            new AgentSubmit(QaReply, 42.5, new AgentUsage(100, 20, 300, 40, 0.01m), 3, null, "fake-agent", "{}"));
        var result = (await submit.Content.ReadFromJsonAsync<AgentSubmitResult>())!;
        Assert.Equal(ReplyCheck.Valid, result.Status);

        await app.WithEval(async e =>
        {
            var saved = (await e.ViewAsync(run.RunId)).Latest!;
            Assert.Equal((Modes.Agent, 42.5, 100L, 0.01m, 3), (saved.Mode, saved.DurationSeconds!.Value, saved.InputTokens!.Value, saved.CostUsd!.Value, saved.NumTurns!.Value));
            var abandoned = await e.ViewAsync(other.RunId);
            Assert.Equal(RunState.Ready, abandoned.State);
        });
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
