using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OBC.Evaluation.Core;
using OBC.Evaluation.Web.Data;

namespace OBC.Evaluation.Web.Services;

/// <summary>What an import changed, and what the person importing should look at.</summary>
public sealed record ImportReport(
    string From, int ModelsAdded, int RunsAdded, int AttemptsAdded, int AttemptsAlreadyHere, int OpenAttemptsSkipped,
    int EventsAdded, List<string> Conflicts, List<string> HashWarnings, List<string> ChecklistNotApplied);

/// <summary>
/// Merges another person's evidence export into this database, so several people can run different models and
/// end up with one record. Ids differ between machines, so everything is matched on what it is: a model by name,
/// version and app; a run by model, task, setup and repeat; an attempt by run, start time and prompt hash.
/// Nothing here is overwritten, and importing the same file twice adds only a line in the event log.
/// </summary>
public sealed class ImportService(IDbContextFactory<EvalDb> dbs, Bundle bundle, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<ImportReport> ImportAsync(Stream package, string fileName)
    {
        var files = await ReadVerifiedAsync(package);
        var manifest = Parse<ManifestFile>(files, "manifest.json");
        if (manifest.Bundle.Sha256 != bundle.Sha256)
            throw new InvalidDataException($"This export was made with bundle {manifest.Bundle.Sha256[..12]}, and this app has {bundle.Sha256[..12]}. Results from different bundles can't be compared; get everyone on the same bundle first.");

        var from = string.IsNullOrWhiteSpace(manifest.ExportedBy) ? fileName : manifest.ExportedBy;
        var attempts = files.Keys.Where(k => k.StartsWith("attempts/", StringComparison.Ordinal) && k.EndsWith("/meta.json", StringComparison.Ordinal))
            .Select(k => (Folder: k[..^"/meta.json".Length], Meta: Parse<AttemptFile>(files, k)))
            .ToList();

        await using var db = await dbs.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();

        var (modelIds, modelsAdded) = await MergeModelsAsync(db, Parse<List<ModelFile>>(files, "models.json"), from);
        var (runs, runsAdded, planted) = await MergeRunsAsync(db, Parse<List<RunFile>>(files, "runs.json"), modelIds, files);
        var merge = await MergeAttemptsAsync(db, attempts, runs, files);

        foreach (var (run, attemptId) in planted)
            run.PlantedFromAttemptId = merge.AttemptIds.GetValueOrDefault(attemptId);
        await db.SaveChangesAsync();

        var eventsAdded = await MergeEventsAsync(db, Parse<List<EventFile>>(files, "events.json"), from);

        var done = (await db.Checklist.Where(c => c.Done).Select(c => c.Id).ToListAsync()).ToHashSet();
        var notApplied = Parse<List<ChecklistFile>>(files, "checklist.json").Where(s => s.Done && !done.Contains(s.Id)).Select(s => s.Id).ToList();

        db.Events.Add(new EventLog
        {
            At = Now, Kind = "import",
            Detail = $"Imported from {from}: {modelsAdded} models, {runsAdded} runs and {merge.Added} attempts added, {merge.AlreadyHere} attempts already here, manifest sha256 {Hashing.Sha256(files["manifest.json"])}.",
        });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        return new ImportReport(from, modelsAdded, runsAdded, merge.Added, merge.AlreadyHere, merge.OpenSkipped, eventsAdded, merge.Conflicts, merge.HashWarnings, notApplied);
    }

    /// <summary>Reads the zip and checks each file against the manifest, so a damaged or edited file stops the import before anything is written.</summary>
    private static async Task<Dictionary<string, byte[]>> ReadVerifiedAsync(Stream package)
    {
        using var buffer = new MemoryStream();
        await package.CopyToAsync(buffer);
        buffer.Position = 0;

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                await using var stream = entry.Open();
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy);
                files[entry.FullName] = copy.ToArray();
            }
        }
        catch (InvalidDataException e)
        {
            throw new InvalidDataException($"This isn't a readable zip: {e.Message}");
        }

        if (!files.ContainsKey("manifest.json"))
            throw new InvalidDataException("This zip has no manifest.json, so it isn't an evidence export.");
        var listed = Parse<ManifestFile>(files, "manifest.json").Files;
        var missing = listed.Keys.Where(k => !files.ContainsKey(k)).ToList();
        var edited = listed.Where(f => files.TryGetValue(f.Key, out var data) && Hashing.Sha256(data) != f.Value).Select(f => f.Key).ToList();
        var extra = files.Keys.Where(k => k != "manifest.json" && !listed.ContainsKey(k)).ToList();
        if (missing.Count + edited.Count + extra.Count > 0)
            throw new InvalidDataException($"This zip doesn't match its manifest. Missing: {Names(missing)}. Changed: {Names(edited)}. Not listed: {Names(extra)}.");
        return files;
    }

    private async Task<(Dictionary<int, int> Ids, int Added)> MergeModelsAsync(EvalDb db, List<ModelFile> incoming, string from)
    {
        var local = await db.Models.ToListAsync();
        var ids = new Dictionary<int, int>();
        var added = 0;
        foreach (var m in incoming)
        {
            var match = local.FirstOrDefault(l => SameModel(l, m));
            if (match == null)
            {
                // Inactive, so it stays out of this person's wizard and runner: its owner runs it, and a second
                // run here would only create a conflict. Tick Active on the Models page to see it on the dashboard.
                match = new ModelProfile
                {
                    Name = m.Name, Provider = m.Provider, ModelVersion = m.ModelVersion, App = m.App, Mode = m.Mode,
                    Notes = string.IsNullOrWhiteSpace(m.Notes) ? $"Imported from {from}." : $"{m.Notes} (Imported from {from}.)",
                    Active = false, CreatedAt = m.CreatedAt,
                };
                db.Models.Add(match);
                local.Add(match);
                added++;
                await db.SaveChangesAsync();
            }
            ids[m.Id] = match.Id;
        }
        return (ids, added);
    }

    private static bool SameModel(ModelProfile local, ModelFile incoming) =>
        Same(local.Name, incoming.Name) && Same(local.ModelVersion, incoming.ModelVersion) && Same(local.App, incoming.App);

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private async Task<(Dictionary<int, Run> Runs, int Added, List<(Run Run, int AttemptId)> Planted)> MergeRunsAsync(
        EvalDb db, List<RunFile> incoming, Dictionary<int, int> modelIds, Dictionary<string, byte[]> files)
    {
        var local = (await db.Runs.ToListAsync()).ToDictionary(r => (r.ModelId, r.TaskId, r.Setup, r.Repeat));
        var runs = new Dictionary<int, Run>();
        var planted = new List<(Run, int)>();
        var added = 0;
        foreach (var r in incoming)
        {
            if (!modelIds.TryGetValue(r.ModelId, out var modelId))
                throw new InvalidDataException($"Run {r.Id} belongs to model {r.ModelId}, which models.json doesn't list.");
            var key = (modelId, r.TaskId, r.Setup, r.Repeat);
            if (!local.TryGetValue(key, out var run))
            {
                run = new Run { ModelId = modelId, TaskId = r.TaskId, Setup = r.Setup, Size = r.Size, Phase = r.Phase, Repeat = r.Repeat, CreatedAt = r.CreatedAt ?? Now };
                db.Runs.Add(run);
                local[key] = run;
                added++;
            }

            // A skip travels one way: un-skipping on one machine doesn't undo it on another
            if (r.Skipped && !run.Skipped)
                (run.Skipped, run.SkipReason) = (true, r.SkipReason);

            if (run.PlantedSource == null && files.TryGetValue($"planted/run-{r.Id:D5}.md", out var source))
            {
                run.PlantedSource = Encoding.UTF8.GetString(source);
                run.PlantedNotes = r.PlantedNotes;
                if (r.PlantedFromAttemptId is { } from)
                    planted.Add((run, from));
            }
            runs[r.Id] = run;
        }
        await db.SaveChangesAsync();
        return (runs, added, planted);
    }

    private sealed record AttemptMerge(Dictionary<int, int> AttemptIds, int Added, int AlreadyHere, int OpenSkipped, List<string> Conflicts, List<string> HashWarnings);

    private static async Task<AttemptMerge> MergeAttemptsAsync(
        EvalDb db, List<(string Folder, AttemptFile Meta)> incoming, Dictionary<int, Run> runs, Dictionary<string, byte[]> files)
    {
        var runIds = runs.Values.Select(r => r.Id).ToHashSet();
        var local = (await db.Attempts.Where(a => runIds.Contains(a.RunId)).ToListAsync()).ToLookup(a => a.RunId);
        var known = local.SelectMany(g => g).ToDictionary(a => (a.RunId, a.StartedAt, a.PromptSha256), a => a.Id);
        var numbers = local.ToDictionary(g => g.Key, g => g.Select(a => a.Number).ToHashSet());
        var incomingKeys = incoming.Where(a => runs.ContainsKey(a.Meta.RunId))
            .Select(a => (runs[a.Meta.RunId].Id, a.Meta.StartedAt, a.Meta.PromptSha256)).ToHashSet();

        var ids = new Dictionary<int, int>();
        var added = new List<(AttemptFile Meta, Attempt Attempt)>();
        var conflicts = new List<string>();
        var warnings = new List<string>();
        int alreadyHere = 0, openSkipped = 0;

        foreach (var (folder, a) in incoming.OrderBy(x => x.Meta.RunId).ThenBy(x => x.Meta.Number))
        {
            if (!runs.TryGetValue(a.RunId, out var run))
                throw new InvalidDataException($"Attempt {a.Id} belongs to run {a.RunId}, which runs.json doesn't list.");

            // Still open on the other machine: it has no result yet, and arrives with a later export once it does
            if (a.Status == AttemptStatus.Open)
            {
                openSkipped++;
                continue;
            }
            if (known.TryGetValue((run.Id, a.StartedAt, a.PromptSha256), out var existing))
            {
                ids[a.Id] = existing;
                alreadyHere++;
                continue;
            }

            var prompt = Text(files, $"{folder}/prompt.md") ?? throw new InvalidDataException($"Attempt {a.Id} has no prompt.md.");
            var reply = Text(files, $"{folder}/reply.md");
            if (Hashing.Sha256(prompt) != a.PromptSha256 || (reply != null && Hashing.Sha256(reply) != a.ReplySha256))
                warnings.Add($"{run.TaskId} {run.Setup}, attempt {a.Number}");

            // Keep the owner's number where it's free. A number taken here means both sides ran this cell
            var taken = numbers.TryGetValue(run.Id, out var set) ? set : numbers[run.Id] = new HashSet<int>();
            var number = taken.Contains(a.Number) ? taken.Max() + 1 : a.Number;
            taken.Add(number);

            var divergent = a.Status == AttemptStatus.Submitted && local[run.Id].Any(l => l.Status == AttemptStatus.Submitted && !incomingKeys.Contains((run.Id, l.StartedAt, l.PromptSha256)));
            if (divergent)
                conflicts.Add($"{run.TaskId} {run.Setup}, model {run.ModelId}: submitted on both machines, attempt {number} now counts");

            var attempt = new Attempt
            {
                RunId = run.Id, Number = number, Status = a.Status, Mode = a.Mode,
                BundleSha256 = a.BundleSha256, SkillName = a.SkillName, SkillSha256 = a.SkillSha256,
                Prompt = prompt, PromptSha256 = a.PromptSha256,
                ModelVersion = a.ModelVersion, App = a.App,
                StartedAt = a.StartedAt, SubmittedAt = a.SubmittedAt, DurationSeconds = a.DurationSeconds,
                Reply = reply, ReplySha256 = a.ReplySha256, Validation = a.Validation,
                ValidationMessages = Joined(a.ValidationMessages), ReceiptIds = Joined(a.ReceiptIds),
                InputTokens = a.InputTokens, OutputTokens = a.OutputTokens, CacheReadTokens = a.CacheReadTokens, CacheCreationTokens = a.CacheCreationTokens,
                CostUsd = a.CostUsd, NumTurns = a.NumTurns, ToolCalls = a.ToolCalls, AgentCommand = a.AgentCommand,
                RawOutput = Text(files, $"{folder}/raw-output.txt"), Notes = a.Notes, AbandonReason = a.AbandonReason,
            };
            db.Attempts.Add(attempt);
            added.Add((a, attempt));
        }
        await db.SaveChangesAsync();

        foreach (var (meta, attempt) in added)
            ids[meta.Id] = attempt.Id;
        // An audit points at its preparer's attempt, which has a different id on this machine
        foreach (var (meta, attempt) in added)
            attempt.SourceAttemptId = meta.SourceAttemptId is { } source ? ids.GetValueOrDefault(source) : null;
        await db.SaveChangesAsync();

        return new AttemptMerge(ids, added.Count, alreadyHere, openSkipped, conflicts, warnings);
    }

    /// <summary>Adds the other person's log, labelled with who it came from. Their checklist ticks and evidence arrive this way.</summary>
    private async Task<int> MergeEventsAsync(EvalDb db, List<EventFile> incoming, string from)
    {
        var known = (await db.Events.ToListAsync()).Select(e => (e.At, e.Kind, Unlabelled(e.Detail))).ToHashSet();
        var added = 0;
        foreach (var e in incoming)
        {
            if (!known.Add((e.At, e.Kind, Unlabelled(e.Detail))))
                continue;
            db.Events.Add(new EventLog { At = e.At, Kind = e.Kind, Detail = e.Detail.StartsWith('[') ? e.Detail : $"[{from}] {e.Detail}" });
            added++;
        }
        await db.SaveChangesAsync();
        return added;
    }

    private static string Unlabelled(string detail)
    {
        var end = detail.StartsWith('[') ? detail.IndexOf("] ", StringComparison.Ordinal) : -1;
        return end < 0 ? detail : detail[(end + 2)..];
    }

    private static T Parse<T>(Dictionary<string, byte[]> files, string path)
    {
        if (!files.TryGetValue(path, out var data))
            throw new InvalidDataException($"This export has no {path}.");
        try
        {
            return JsonSerializer.Deserialize<T>(data, Json) ?? throw new InvalidDataException($"{path} is empty.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{path} can't be read: {e.Message}");
        }
    }

    private static string? Text(Dictionary<string, byte[]> files, string path) =>
        files.TryGetValue(path, out var data) ? Encoding.UTF8.GetString(data) : null;

    private static string? Joined(List<string>? lines) => lines is { Count: > 0 } ? string.Join("\n", lines) : null;

    private static string Names(List<string> names) => names.Count == 0 ? "none" : string.Join(", ", names.Take(5)) + (names.Count > 5 ? $" and {names.Count - 5} more" : "");

    private sealed record ManifestFile(string? ExportedBy, ManifestBundle Bundle, Dictionary<string, string> Files);
    private sealed record ManifestBundle(string Sha256);
    private sealed record ModelFile(int Id, string Name, string Provider, string ModelVersion, string App, string Mode, string? Notes, DateTime CreatedAt);
    private sealed record RunFile(int Id, int ModelId, string TaskId, string Setup, int Size, string Phase, int Repeat, bool Skipped, string? SkipReason, DateTime? CreatedAt, int? PlantedFromAttemptId, string? PlantedNotes);
    private sealed record ChecklistFile(string Id, bool Done);
    private sealed record EventFile(DateTime At, string Kind, string Detail);
    private sealed record AttemptFile(
        int Id, int RunId, int Number, string Status, string Mode, string ModelVersion, string App, string BundleSha256, string SkillName, string SkillSha256,
        string PromptSha256, string? ReplySha256, int? SourceAttemptId, DateTime StartedAt, DateTime? SubmittedAt, double? DurationSeconds, string? Validation,
        List<string>? ValidationMessages, List<string>? ReceiptIds, long? InputTokens, long? OutputTokens, long? CacheReadTokens, long? CacheCreationTokens,
        decimal? CostUsd, int? NumTurns, int? ToolCalls, string? AgentCommand, string? Notes, string? AbandonReason);
}
