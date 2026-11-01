using Microsoft.EntityFrameworkCore;
using OBC.Evaluation.Core;
using OBC.Evaluation.Web.Data;

namespace OBC.Evaluation.Web.Services;

public enum RunState { Ready, Blocked, InProgress, Done, Skipped }

public sealed record RunView(Run Run, EvalTask Task, RunState State, string? BlockedBy, Attempt? Latest);

/// <summary>
/// The evaluation's rules in one place: which runs exist, which can start, what each is given, and what is saved.
/// The wizard and the agent API both go through it, so a manual and an agent run follow the same rules.
/// </summary>
public sealed class EvalService(IDbContextFactory<EvalDb> dbs, Bundle bundle, TimeProvider clock)
{
    /// <summary>An agent attempt left open this long is taken as crashed, and its run is offered again.</summary>
    public static readonly TimeSpan StaleAgentAttempt = TimeSpan.FromHours(3);

    private static readonly Dictionary<string, string[]> Prerequisites = new()
    {
        ["T4"] = ["T1", "T2", "T3"],
        ["T7"] = ["T5", "T6"],
    };

    public Bundle Bundle => bundle;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ------------------------------------------------------------------ models and the run matrix

    public async Task<ModelProfile> AddModelAsync(ModelProfile model)
    {
        await using var db = await dbs.CreateDbContextAsync();
        model.CreatedAt = Now;
        db.Models.Add(model);
        await db.SaveChangesAsync();
        await EnsureRunsAsync(model.Id);
        await LogAsync("model", $"Added {model.Name} ({model.ModelVersion}, {model.App}, {model.Mode}).");
        return model;
    }

    /// <summary>Creates the model's runs for every task in the matrix. Safe to call again; it only adds what is missing.</summary>
    public async Task<int> EnsureRunsAsync(int modelId)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var existing = (await db.Runs.Where(r => r.ModelId == modelId && r.Repeat == 1).Select(r => new { r.TaskId, r.Setup }).ToListAsync())
            .Select(r => (r.TaskId, r.Setup)).ToHashSet();
        var added = 0;
        foreach (var task in bundle.Tasks.Where(t => !t.HeldOut))
        foreach (var setup in task.Setups.Where(s => !existing.Contains((task.Id, s))))
        {
            db.Runs.Add(new Run { ModelId = modelId, TaskId = task.Id, Setup = setup, Size = task.Size, Phase = task.Phase, CreatedAt = Now });
            added++;
        }
        await db.SaveChangesAsync();
        return added;
    }

    /// <summary>Adds a held-out instance or a repeat for variance, as an extra run.</summary>
    public async Task<Run> AddRunAsync(int modelId, string taskId, string setup)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var task = bundle.Task(taskId);
        var repeat = await db.Runs.Where(r => r.ModelId == modelId && r.TaskId == taskId && r.Setup == setup).MaxAsync(r => (int?)r.Repeat) ?? 0;
        var run = new Run { ModelId = modelId, TaskId = taskId, Setup = setup, Size = task.Size, Phase = task.Phase, Repeat = repeat + 1, CreatedAt = Now };
        db.Runs.Add(run);
        await db.SaveChangesAsync();
        await LogAsync("run", $"Added {taskId} {setup} repeat {run.Repeat} for model {modelId}.");
        return run;
    }

    // ------------------------------------------------------------------ state

    public async Task<List<RunView>> ViewsAsync(int? modelId = null)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var runs = await db.Runs.Include(r => r.Model).Include(r => r.Attempts)
            .Where(r => modelId == null || r.ModelId == modelId).ToListAsync();
        var done = await DoneStepsAsync();
        var order = bundle.Tasks.Select((t, i) => (t.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var submitted = runs.Where(r => r.Attempts.Any(a => a.Status == AttemptStatus.Submitted))
            .ToDictionary(r => (r.ModelId, r.TaskId, r.Setup, r.Repeat), r => Latest(r)!);
        return runs
            .OrderBy(r => r.Model.Name).ThenBy(r => r.Phase).ThenBy(r => r.Size)
            .ThenBy(r => order.GetValueOrDefault(r.TaskId, int.MaxValue))
            .ThenBy(r => bundle.Setups.IndexOf(r.Setup)).ThenBy(r => r.Repeat)
            .Select(r => View(r, done, submitted)).ToList();
    }

    public async Task<RunView> ViewAsync(int runId)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var run = await db.Runs.FindAsync(runId) ?? throw new KeyNotFoundException($"No run {runId}.");
        return (await ViewsAsync(run.ModelId)).First(v => v.Run.Id == runId);
    }

    private RunView View(Run run, HashSet<string> done, Dictionary<(int, string, string, int), Attempt> submitted)
    {
        var task = bundle.Task(run.TaskId);
        var latest = Latest(run);
        if (run.Skipped)
            return new RunView(run, task, RunState.Skipped, run.SkipReason, latest);
        if (run.Attempts.Any(a => a.Status == AttemptStatus.Submitted))
            return new RunView(run, task, RunState.Done, null, latest);
        if (run.Attempts.Any(a => a.Status == AttemptStatus.Open))
            return new RunView(run, task, RunState.InProgress, null, latest);
        var blocked = BlockedBy(run, task, done, submitted);
        return new RunView(run, task, blocked == null ? RunState.Ready : RunState.Blocked, blocked, latest);
    }

    private string? BlockedBy(Run run, EvalTask task, HashSet<string> done, Dictionary<(int, string, string, int), Attempt> submitted)
    {
        if (Prerequisites.TryGetValue(task.Phase, out var steps))
        {
            var missing = steps.Where(s => !done.Contains($"{s}@{task.Size}")).ToList();
            if (missing.Count > 0)
                return $"Checklist {string.Join(", ", missing)} at {task.Size:N0} not done";
        }
        if (task.DependsOn != null)
        {
            if (!submitted.TryGetValue((run.ModelId, task.DependsOn, run.Setup, 1), out var source))
                return $"Waiting for {task.DependsOn} ({run.Setup})";
            if (!HasAuditSource(source.Reply))
                return $"{task.DependsOn} ({run.Setup}) has no REPORT and WORKING PAPERS to audit; skip this run or retry the preparer";
        }
        if (task.Kind == TaskKinds.PlantedAudit && string.IsNullOrWhiteSpace(run.PlantedSource))
            return "Plant the errors in a copy of the preparer's output first";
        return null;
    }

    private static bool HasAuditSource(string? reply)
    {
        var blocks = Blocks.Parse(reply ?? "");
        return blocks.ContainsKey(Blocks.Report) && blocks.ContainsKey(Blocks.WorkingPapers);
    }

    private static Attempt? Latest(Run run) =>
        run.Attempts.Where(a => a.Status == AttemptStatus.Submitted).MaxBy(a => a.Number)
        ?? run.Attempts.MaxBy(a => a.Number);

    public async Task<HashSet<string>> DoneStepsAsync()
    {
        await using var db = await dbs.CreateDbContextAsync();
        return (await db.Checklist.Where(c => c.Done).Select(c => c.Id).ToListAsync()).ToHashSet();
    }

    /// <summary>The next run that can start, in matrix order: phase, size, task, setup.</summary>
    public async Task<RunView?> NextAsync(int modelId, int? size = null, string? setup = null, bool includeInProgress = true)
    {
        await AbandonStaleAgentAttemptsAsync();
        var views = await ViewsAsync(modelId);
        var candidates = views.Where(v => (size == null || v.Run.Size == size) && (setup == null || v.Run.Setup == setup));
        return (includeInProgress ? candidates.FirstOrDefault(v => v.State == RunState.InProgress) : null)
               ?? candidates.FirstOrDefault(v => v.State == RunState.Ready);
    }

    // ------------------------------------------------------------------ attempts

    /// <summary>
    /// Opens an attempt for a run, or returns the one already open, with the exact text the model is given.
    /// A done run starts again only as a retry, for example after a broken paste; the latest submitted attempt counts.
    /// </summary>
    public async Task<Attempt> StartAsync(int runId, string mode, bool retry = false)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var view = await ViewAsync(runId);
        var open = view.Run.Attempts.FirstOrDefault(a => a.Status == AttemptStatus.Open);
        if (open != null)
            return open;
        if (view.State != RunState.Ready && !(retry && view.State == RunState.Done))
            throw new InvalidOperationException($"Run {runId} can't start: {view.BlockedBy ?? view.State.ToString()}.");

        var (source, sourceAttemptId) = await AuditSourceAsync(view);
        var skillName = view.Task.SkillFor(view.Run.Setup, bundle.QaSkill);
        var prompt = PromptBuilder.Build(view.Task, view.Run.Setup, bundle.SkillTexts[skillName], source);
        var attempt = new Attempt
        {
            RunId = runId,
            Number = view.Run.Attempts.Count + 1,
            Mode = mode,
            BundleSha256 = bundle.Sha256,
            SkillName = skillName,
            SkillSha256 = bundle.Skill(skillName).Sha256,
            Prompt = prompt,
            PromptSha256 = Hashing.Sha256(prompt),
            SourceAttemptId = sourceAttemptId,
            ModelVersion = view.Run.Model.ModelVersion,
            App = view.Run.Model.App,
            StartedAt = Now,
        };
        db.Attempts.Add(attempt);
        await db.SaveChangesAsync();
        return attempt;
    }

    private async Task<(string? Source, int? AttemptId)> AuditSourceAsync(RunView view)
    {
        if (!TaskKinds.IsAudit(view.Task.Kind))
            return (null, null);
        if (view.Task.Kind == TaskKinds.PlantedAudit)
            return (view.Run.PlantedSource, view.Run.PlantedFromAttemptId);
        var preparer = await PreparerAttemptAsync(view.Run.ModelId, view.Task.DependsOn!, view.Run.Setup);
        return (PromptBuilder.AuditSource(preparer!.Reply!), preparer.Id);
    }

    /// <summary>The scored (repeat 1) preparer run's latest submitted attempt.</summary>
    public async Task<Attempt?> PreparerAttemptAsync(int modelId, string taskId, string setup)
    {
        await using var db = await dbs.CreateDbContextAsync();
        return await db.Attempts.Where(a => a.Run.ModelId == modelId && a.Run.TaskId == taskId && a.Run.Setup == setup && a.Run.Repeat == 1
                                            && a.Status == AttemptStatus.Submitted)
            .OrderByDescending(a => a.Number).FirstOrDefaultAsync();
    }

    public async Task<ReplyCheck> SubmitAsync(int attemptId, string reply, Action<Attempt>? measure = null)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var attempt = await db.Attempts.Include(a => a.Run).FirstAsync(a => a.Id == attemptId);
        if (attempt.Status != AttemptStatus.Open)
            throw new InvalidOperationException($"Attempt {attemptId} is {attempt.Status}; start a new attempt instead.");

        var task = bundle.Task(attempt.Run.TaskId);
        var check = ReplyValidator.Check(task.Kind, attempt.Run.Setup, reply);
        attempt.Reply = reply;
        attempt.ReplySha256 = Hashing.Sha256(reply);
        attempt.Validation = check.Status;
        attempt.ValidationMessages = check.Messages.Count > 0 ? string.Join("\n", check.Messages) : null;
        attempt.ReceiptIds = check.ReceiptIds.Count > 0 ? string.Join("\n", check.ReceiptIds) : null;
        attempt.SubmittedAt = Now;
        attempt.DurationSeconds ??= (attempt.SubmittedAt - attempt.StartedAt).Value.TotalSeconds;
        measure?.Invoke(attempt);
        attempt.Status = AttemptStatus.Submitted;
        await db.SaveChangesAsync();
        await LogAsync("submit", $"Attempt {attempt.Id} ({attempt.Run.TaskId} {attempt.Run.Setup}, model {attempt.Run.ModelId}): {check.Status}.");
        return check;
    }

    public async Task AbandonAsync(int attemptId, string reason, string? rawOutput = null)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var attempt = await db.Attempts.Include(a => a.Run).FirstAsync(a => a.Id == attemptId);
        if (attempt.Status != AttemptStatus.Open)
            return;
        attempt.Status = AttemptStatus.Abandoned;
        attempt.AbandonReason = reason;
        attempt.RawOutput ??= rawOutput;
        attempt.SubmittedAt = Now;
        await db.SaveChangesAsync();
        await LogAsync("abandon", $"Attempt {attempt.Id} ({attempt.Run.TaskId} {attempt.Run.Setup}): {reason}");
    }

    private async Task AbandonStaleAgentAttemptsAsync()
    {
        await using var db = await dbs.CreateDbContextAsync();
        var cutoff = Now - StaleAgentAttempt;
        var stale = await db.Attempts.Where(a => a.Status == AttemptStatus.Open && a.Mode == Modes.Agent && a.StartedAt < cutoff)
            .Select(a => a.Id).ToListAsync();
        foreach (var id in stale)
            await AbandonAsync(id, $"No result within {StaleAgentAttempt.TotalHours:0} hours; the runner probably stopped.");
    }

    // ------------------------------------------------------------------ run edits

    public async Task SkipAsync(int runId, string reason)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var run = await db.Runs.FindAsync(runId) ?? throw new KeyNotFoundException();
        run.Skipped = true;
        run.SkipReason = reason;
        await db.SaveChangesAsync();
        await LogAsync("skip", $"Run {runId} ({run.TaskId} {run.Setup}) skipped: {reason}");
    }

    public async Task UnskipAsync(int runId)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var run = await db.Runs.FindAsync(runId) ?? throw new KeyNotFoundException();
        run.Skipped = false;
        run.SkipReason = null;
        await db.SaveChangesAsync();
        await LogAsync("skip", $"Run {runId} ({run.TaskId} {run.Setup}) back in the matrix.");
    }

    public async Task SavePlantedAsync(int runId, string source, int fromAttemptId, string? notes)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var run = await db.Runs.FindAsync(runId) ?? throw new KeyNotFoundException();
        if (await db.Attempts.AnyAsync(a => a.RunId == runId))
            throw new InvalidOperationException("This run already has attempts; its planted copy can't change.");
        var from = await db.Attempts.Include(a => a.Run).FirstOrDefaultAsync(a => a.Id == fromAttemptId);
        if (from?.Run.ModelId != run.ModelId || from.Run.Setup != run.Setup || from.Run.TaskId != bundle.Task(run.TaskId).DependsOn)
            throw new InvalidOperationException("The planted copy must come from this model's preparer run in the same setup.");
        run.PlantedSource = source;
        run.PlantedFromAttemptId = fromAttemptId;
        run.PlantedNotes = notes;
        await db.SaveChangesAsync();
        await LogAsync("plant", $"Run {runId}: planted copy saved from attempt {fromAttemptId} (sha256 {Hashing.Sha256(source)}).");
    }

    public async Task SetStepAsync(string id, bool done, string? evidence)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var state = await db.Checklist.FindAsync(id);
        if (state == null)
            db.Checklist.Add(state = new ChecklistState { Id = id });
        state.Done = done;
        state.DoneAt = done ? Now : null;
        state.Evidence = evidence;
        await db.SaveChangesAsync();
        await LogAsync("checklist", $"{id} {(done ? "done" : "reopened")}{(string.IsNullOrWhiteSpace(evidence) ? "" : $": {evidence}")}");
    }

    public async Task LogAsync(string kind, string detail)
    {
        await using var db = await dbs.CreateDbContextAsync();
        db.Events.Add(new EventLog { At = Now, Kind = kind, Detail = detail });
        await db.SaveChangesAsync();
    }
}
