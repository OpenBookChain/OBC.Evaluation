using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using OBC.Evaluation.Core;
using OBC.Evaluation.Web.Data;
using OBC.Evaluation.Web.Services;

namespace OBC.Evaluation.Tests;

/// <summary>Two machines, each with its own database, syncing through evidence exports.</summary>
public sealed class SyncTests : IDisposable
{
    private readonly AppFactory alice = new();
    private readonly AppFactory bob = new();

    public void Dispose()
    {
        alice.Dispose();
        bob.Dispose();
    }

    private static Task<ModelProfile> AddModel(AppFactory app, string name) => app.WithEval(e => e.AddModelAsync(new ModelProfile
    {
        Name = name, Provider = "Test", ModelVersion = "test-1", App = "Test app", Mode = Modes.Manual,
    }));

    private static Task TickAsync(AppFactory app) =>
        app.WithEval(async e => { foreach (var s in new[] { "T1", "T2", "T3" }) await e.SetStepAsync($"{s}@5000", true, "test"); });

    /// <summary>Saves a preparer and its audit for the model, so one attempt points at another by id.</summary>
    private static async Task RunAuditAsync(AppFactory app, int modelId) => await app.WithEval(async e =>
    {
        var views = await e.ViewsAsync(modelId);
        var prepare = views.First(v => v.Task.Id == "C1-prepare@5000" && v.Run.Setup == "obc");
        await e.SubmitAsync((await e.StartAsync(prepare.Run.Id, Modes.Manual)).Id, FlowTests.PreparerReply);
        var audit = views.First(v => v.Task.Id == "C1-audit@5000" && v.Run.Setup == "obc");
        await e.SubmitAsync((await e.StartAsync(audit.Run.Id, Modes.Manual)).Id, FlowTests.PreparerReply);
    });

    private static async Task<byte[]> ExportAsync(AppFactory app) => await app.CreateClient().GetByteArrayAsync("export.zip");

    private static async Task<ImportReport> ImportAsync(AppFactory app, byte[] package)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ImportService>().ImportAsync(new MemoryStream(package), "test.zip");
    }

    private static byte[] Rewrite(byte[] package, Func<string, byte[], byte[]> change)
    {
        using var output = new MemoryStream();
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        using (var source = new ZipArchive(new MemoryStream(package)))
        {
            foreach (var entry in source.Entries)
            {
                using var read = entry.Open();
                using var copy = new MemoryStream();
                read.CopyTo(copy);
                using var write = target.CreateEntry(entry.FullName).Open();
                write.Write(change(entry.FullName, copy.ToArray()));
            }
        }
        return output.ToArray();
    }

    [Fact]
    public async Task Each_persons_models_end_up_in_one_database_with_their_links_intact()
    {
        var aliceModel = await AddModel(alice, "Vendor A");
        await TickAsync(alice);
        await RunAuditAsync(alice, aliceModel.Id);

        // Bob's own model takes the ids Alice's rows have, so nothing can match by id
        var bobModel = await AddModel(bob, "Vendor B");
        await TickAsync(bob);
        await RunAuditAsync(bob, bobModel.Id);

        var report = await ImportAsync(bob, await ExportAsync(alice));

        Assert.Equal(1, report.ModelsAdded);
        Assert.Equal(2, report.AttemptsAdded);
        Assert.Empty(report.Conflicts);
        Assert.Empty(report.HashWarnings);

        await bob.WithEval(async e =>
        {
            var imported = (await e.ViewsAsync()).Where(v => v.Run.Model.Name == "Vendor A").ToList();
            Assert.Equal(145, imported.Count);
            Assert.False(imported[0].Run.Model.Active);
            var prepare = imported.First(v => v.Task.Id == "C1-prepare@5000" && v.Run.Setup == "obc");
            var audit = imported.First(v => v.Task.Id == "C1-audit@5000" && v.Run.Setup == "obc");
            Assert.Equal(RunState.Done, audit.State);
            Assert.NotEqual(aliceModel.Id, audit.Run.ModelId);
            Assert.Equal(prepare.Latest!.Id, audit.Latest!.SourceAttemptId);
            Assert.Equal(Hashing.Sha256(audit.Latest.Prompt), audit.Latest.PromptSha256);

            var own = (await e.ViewsAsync(bobModel.Id)).First(v => v.Task.Id == "C1-audit@5000" && v.Run.Setup == "obc");
            Assert.Equal(RunState.Done, own.State);
            Assert.Equal(1, own.Latest!.Number);
        });
    }

    [Fact]
    public async Task Importing_the_same_export_twice_changes_nothing()
    {
        var model = await AddModel(alice, "Vendor A");
        await TickAsync(alice);
        await RunAuditAsync(alice, model.Id);
        var package = await ExportAsync(alice);

        await ImportAsync(bob, package);
        var again = await ImportAsync(bob, package);

        Assert.Equal((0, 0, 0, 2, 0), (again.ModelsAdded, again.RunsAdded, again.AttemptsAdded, again.AttemptsAlreadyHere, again.EventsAdded));
    }

    [Fact]
    public async Task A_round_trip_through_a_teammate_does_not_duplicate_anything()
    {
        var model = await AddModel(alice, "Vendor A");
        await TickAsync(alice);
        await RunAuditAsync(alice, model.Id);

        await ImportAsync(bob, await ExportAsync(alice));
        var back = await ImportAsync(alice, await ExportAsync(bob));

        Assert.Equal((0, 0, 0, 0), (back.ModelsAdded, back.RunsAdded, back.AttemptsAdded, back.Conflicts.Count));
    }

    [Fact]
    public async Task A_later_export_brings_the_owners_retry_without_calling_it_a_conflict()
    {
        var model = await AddModel(alice, "Vendor A");
        await TickAsync(alice);
        await RunAuditAsync(alice, model.Id);
        await ImportAsync(bob, await ExportAsync(alice));

        await alice.WithEval(async e =>
        {
            var prepare = (await e.ViewsAsync(model.Id)).First(v => v.Task.Id == "C1-prepare@5000" && v.Run.Setup == "obc");
            await e.SubmitAsync((await e.StartAsync(prepare.Run.Id, Modes.Manual, retry: true)).Id, FlowTests.PreparerReply);
        });
        var report = await ImportAsync(bob, await ExportAsync(alice));

        Assert.Equal(1, report.AttemptsAdded);
        Assert.Empty(report.Conflicts);
    }

    [Fact]
    public async Task The_same_cell_run_on_two_machines_keeps_both_attempts_and_is_reported()
    {
        var aliceModel = await AddModel(alice, "Shared");
        await TickAsync(alice);
        await RunAuditAsync(alice, aliceModel.Id);
        var bobModel = await AddModel(bob, "Shared");
        await TickAsync(bob);
        await RunAuditAsync(bob, bobModel.Id);

        var report = await ImportAsync(bob, await ExportAsync(alice));

        Assert.Equal(0, report.ModelsAdded);
        Assert.Equal(2, report.Conflicts.Count);
        await bob.WithEval(async e =>
        {
            var prepare = (await e.ViewsAsync(bobModel.Id)).First(v => v.Task.Id == "C1-prepare@5000" && v.Run.Setup == "obc");
            Assert.Equal(new[] { 1, 2 }, prepare.Run.Attempts.Select(a => a.Number).Order());
        });
    }

    [Fact]
    public async Task A_teammates_checklist_does_not_unlock_runs_here()
    {
        var model = await AddModel(alice, "Vendor A");
        await TickAsync(alice);
        await RunAuditAsync(alice, model.Id);

        var report = await ImportAsync(bob, await ExportAsync(alice));

        Assert.Contains("T1@5000", report.ChecklistNotApplied);
        await bob.WithEval(async e => Assert.DoesNotContain("T1@5000", await e.DoneStepsAsync()));
    }

    [Fact]
    public async Task An_edited_file_stops_the_import_before_anything_is_written()
    {
        var model = await AddModel(alice, "Vendor A");
        await TickAsync(alice);
        await RunAuditAsync(alice, model.Id);
        var edited = Rewrite(await ExportAsync(alice), (path, data) => path.EndsWith("/reply.md") ? Encoding.UTF8.GetBytes("edited") : data);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync(bob, edited));

        Assert.Contains("doesn't match its manifest", error.Message);
        await bob.WithEval(async e => Assert.Empty(await e.ViewsAsync()));
    }

    [Fact]
    public async Task An_export_from_a_different_bundle_is_refused()
    {
        await AddModel(alice, "Vendor A");
        var other = Rewrite(await ExportAsync(alice), (path, data) =>
        {
            if (path != "manifest.json")
                return data;
            var manifest = JsonNode.Parse(data)!;
            manifest["bundle"]!["sha256"] = new string('0', 64);
            return Encoding.UTF8.GetBytes(manifest.ToJsonString());
        });

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync(bob, other));

        Assert.Contains("different bundles", error.Message);
    }

    [Fact]
    public async Task A_file_that_is_not_an_export_is_refused()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync(bob, Encoding.UTF8.GetBytes("not a zip")));
    }
}
