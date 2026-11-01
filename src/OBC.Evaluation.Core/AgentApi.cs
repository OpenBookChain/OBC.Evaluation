namespace OBC.Evaluation.Core;

/// <summary>
/// Contracts of the agent API. A runner asks for the next run, gives the prompt to a fresh agent process and
/// posts back what the agent replied. The API never returns answers or scores.
/// </summary>
public static class AgentApi
{
    public const string KeyHeader = "X-Api-Key";
}

public sealed record AgentRun(
    int AttemptId,
    int RunId,
    string TaskId,
    string Kind,
    int Size,
    string Setup,
    string Prompt,
    string PromptSha256,
    AgentDataFile? DataFile);

/// <summary>The CSV a CSV-setup run works from: its folder under the data root, its name, and the SHA-256 it must have.</summary>
public sealed record AgentDataFile(string Folder, string Name, string Sha256);

public sealed record AgentUsage(long? InputTokens, long? OutputTokens, long? CacheReadTokens, long? CacheCreationTokens, decimal? CostUsd);

public sealed record AgentSubmit(
    string Reply,
    double DurationSeconds,
    AgentUsage? Usage,
    int? NumTurns,
    int? ToolCalls,
    string? AgentCommand,
    string? RawOutput);

public sealed record AgentAbandon(string Reason, string? RawOutput);

public sealed record AgentSubmitResult(string Status, List<string> Messages);
