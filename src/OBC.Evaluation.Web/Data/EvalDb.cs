using Microsoft.EntityFrameworkCore;

namespace OBC.Evaluation.Web.Data;

public sealed class EvalDb(DbContextOptions<EvalDb> options) : DbContext(options)
{
    public DbSet<ModelProfile> Models => Set<ModelProfile>();
    public DbSet<Run> Runs => Set<Run>();
    public DbSet<Attempt> Attempts => Set<Attempt>();
    public DbSet<ChecklistState> Checklist => Set<ChecklistState>();
    public DbSet<EventLog> Events => Set<EventLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Run>().HasIndex(r => new { r.ModelId, r.TaskId, r.Setup, r.Repeat }).IsUnique();
        b.Entity<Run>().HasMany(r => r.Attempts).WithOne(a => a.Run).HasForeignKey(a => a.RunId);
        b.Entity<Run>().HasOne(r => r.Model).WithMany().HasForeignKey(r => r.ModelId);
        b.Entity<Attempt>().HasIndex(a => new { a.RunId, a.Number }).IsUnique();
        b.Entity<Attempt>().Property(a => a.CostUsd).HasConversion<double?>();
    }
}

public static class Modes
{
    public const string Manual = "manual";
    public const string Agent = "agent";
}

/// <summary>A model as it is run: the exact model, the app or tool it runs in, and whether a person or an agent runner drives it.</summary>
public sealed class ModelProfile
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;

    /// <summary>The exact model name and version, e.g. claude-opus-5-5 or gpt-5.1-2026-08-01.</summary>
    public string ModelVersion { get; set; } = string.Empty;

    /// <summary>Where it runs: ChatGPT web, Claude.ai, Gemini app, Claude Code CLI, Copilot CLI, API…</summary>
    public string App { get; set; } = string.Empty;

    public string Mode { get; set; } = Modes.Manual;
    public string? Notes { get; set; }
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}

/// <summary>One cell of the run matrix: a task, in one setup, for one model. Repeat 1 is the scored run; more repeats measure variance.</summary>
public sealed class Run
{
    public int Id { get; set; }
    public int ModelId { get; set; }
    public ModelProfile Model { get; set; } = null!;
    public string TaskId { get; set; } = string.Empty;
    public string Setup { get; set; } = string.Empty;
    public int Size { get; set; }
    public string Phase { get; set; } = string.Empty;
    public int Repeat { get; set; } = 1;
    public bool Skipped { get; set; }
    public string? SkipReason { get; set; }

    /// <summary>A planted-error audit's source: a copy of the preparer's report and working papers with the errors planted.</summary>
    public string? PlantedSource { get; set; }
    public int? PlantedFromAttemptId { get; set; }
    public string? PlantedNotes { get; set; }

    public DateTime CreatedAt { get; set; }
    public List<Attempt> Attempts { get; set; } = new();
}

public static class AttemptStatus
{
    public const string Open = "open";
    public const string Submitted = "submitted";
    public const string Abandoned = "abandoned";
}

/// <summary>One try at a run: what was given, what came back, and how it was measured. Nothing is ever overwritten; a retry is a new attempt.</summary>
public sealed class Attempt
{
    public int Id { get; set; }
    public int RunId { get; set; }
    public Run Run { get; set; } = null!;
    public int Number { get; set; }
    public string Status { get; set; } = AttemptStatus.Open;
    public string Mode { get; set; } = Modes.Manual;

    public string BundleSha256 { get; set; } = string.Empty;
    public string SkillName { get; set; } = string.Empty;
    public string SkillSha256 { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string PromptSha256 { get; set; } = string.Empty;

    /// <summary>For an audit: the preparer attempt whose report and working papers it was given.</summary>
    public int? SourceAttemptId { get; set; }

    public string ModelVersion { get; set; } = string.Empty;
    public string App { get; set; } = string.Empty;

    public DateTime StartedAt { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public double? DurationSeconds { get; set; }

    public string? Reply { get; set; }
    public string? ReplySha256 { get; set; }
    public string? Validation { get; set; }
    public string? ValidationMessages { get; set; }
    public string? ReceiptIds { get; set; }

    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public long? CacheReadTokens { get; set; }
    public long? CacheCreationTokens { get; set; }
    public decimal? CostUsd { get; set; }
    public int? NumTurns { get; set; }
    public int? ToolCalls { get; set; }
    public string? AgentCommand { get; set; }
    public string? RawOutput { get; set; }

    public string? Notes { get; set; }
    public string? AbandonReason { get; set; }
}

/// <summary>A setup step per size (T1 load, T2 close, …), ticked with the evidence that it was done.</summary>
public sealed class ChecklistState
{
    public string Id { get; set; } = string.Empty;
    public bool Done { get; set; }
    public DateTime? DoneAt { get; set; }
    public string? Evidence { get; set; }
}

/// <summary>An append-only log of what happened, exported with the evidence.</summary>
public sealed class EventLog
{
    public int Id { get; set; }
    public DateTime At { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}
