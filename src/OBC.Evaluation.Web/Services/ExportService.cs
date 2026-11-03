using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OBC.Evaluation.Core;
using OBC.Evaluation.Web.Data;

namespace OBC.Evaluation.Web.Services;

/// <summary>
/// Writes everything recorded so far as an evidence package for the public repo: the bundle that was run, the
/// models, the checklist, every run and attempt with its exact prompt and reply, and the event log. Each file's
/// SHA-256 is listed in manifest.json, so anyone can check nothing was edited after export.
/// </summary>
public sealed class ExportService(IDbContextFactory<EvalDb> dbs, Bundle bundle, TimeProvider clock, IConfiguration config)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly DateTime ZipTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public async Task<byte[]> ExportAsync()
    {
        await using var db = await dbs.CreateDbContextAsync();
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        void Add(string path, string text) => files[path] = Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"));
        void AddJson(string path, object value) => Add(path, JsonSerializer.Serialize(value, Json) + "\n");

        // The bundle as run. The answer key stays out: publish it separately once every run is done.
        Add("bundle/bundle.json", await File.ReadAllTextAsync(Path.Combine(bundle.Folder, "bundle.json")));
        foreach (var skill in bundle.Skills)
            Add($"bundle/{skill.File}", bundle.SkillTexts[skill.Name]);

        var models = await db.Models.OrderBy(m => m.Id).ToListAsync();
        AddJson("models.json", models.Select(m => new { m.Id, m.Name, m.Provider, m.ModelVersion, m.App, m.Mode, m.AgentCli, m.AgentConfigJson, m.Notes, m.Active, m.CreatedAt }));

        var steps = await db.Checklist.ToDictionaryAsync(c => c.Id);
        AddJson("checklist.json", bundle.Checklist.Select(s => new
        {
            s.Id, s.Size, s.Step, s.Title, s.Details,
            Done = steps.GetValueOrDefault(s.Id)?.Done ?? false,
            steps.GetValueOrDefault(s.Id)?.DoneAt,
            steps.GetValueOrDefault(s.Id)?.Evidence,
        }));

        var runs = await db.Runs.Include(r => r.Attempts).OrderBy(r => r.Id).ToListAsync();
        AddJson("runs.json", runs.Select(r => new
        {
            r.Id, r.ModelId, r.TaskId, r.Setup, r.Size, r.Phase, r.Repeat, r.Skipped, r.SkipReason, r.CreatedAt,
            PlantedSourceSha256 = r.PlantedSource == null ? null : Hashing.Sha256(r.PlantedSource),
            r.PlantedFromAttemptId, r.PlantedNotes,
            Attempts = r.Attempts.OrderBy(a => a.Number).Select(a => $"attempts/{a.Id:D5}").ToList(),
        }));
        foreach (var run in runs.Where(r => r.PlantedSource != null))
            Add($"planted/run-{run.Id:D5}.md", run.PlantedSource!);

        foreach (var a in runs.SelectMany(r => r.Attempts))
        {
            var folder = $"attempts/{a.Id:D5}";
            Add($"{folder}/prompt.md", a.Prompt);
            if (a.Reply != null)
                Add($"{folder}/reply.md", a.Reply);
            if (a.RawOutput != null)
                Add($"{folder}/raw-output.txt", a.RawOutput);
            AddJson($"{folder}/meta.json", new
            {
                a.Id, a.RunId, a.Run.ModelId, a.Run.TaskId, a.Run.Setup, a.Run.Size, a.Run.Repeat, a.Number,
                a.Status, a.Mode, a.ModelVersion, a.App, a.BundleSha256, a.SkillName, a.SkillSha256, a.PromptSha256,
                a.ReplySha256, a.SourceAttemptId, a.StartedAt, a.SubmittedAt, a.DurationSeconds, a.Validation,
                ValidationMessages = Lines(a.ValidationMessages), ReceiptIds = Lines(a.ReceiptIds),
                a.InputTokens, a.OutputTokens, a.CacheReadTokens, a.CacheCreationTokens, a.CostUsd, a.NumTurns, a.ToolCalls,
                a.AgentCommand, a.Notes, a.AbandonReason,
            });
        }

        AddJson("events.json", await db.Events.OrderBy(e => e.Id).ToListAsync());

        var manifest = new
        {
            exportedAt = clock.GetUtcNow().UtcDateTime,
            exportedBy = config["Evaluation:Operator"],
            bundle = new { bundle.Name, sha256 = bundle.Sha256, answerKeySha256 = bundle.AnswerKey.Sha256 },
            counts = new
            {
                models = models.Count,
                runs = runs.Count,
                attempts = runs.Sum(r => r.Attempts.Count),
                submitted = runs.Sum(r => r.Attempts.Count(a => a.Status == AttemptStatus.Submitted)),
            },
            files = files.ToDictionary(f => f.Key, f => Hashing.Sha256(f.Value)),
        };
        AddJson("manifest.json", manifest);

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, data) in files)
            {
                var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                entry.LastWriteTime = ZipTime;
                await using var stream = entry.Open();
                await stream.WriteAsync(data);
            }
        }
        return buffer.ToArray();
    }

    private static List<string> Lines(string? text) =>
        text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() ?? new();
}
