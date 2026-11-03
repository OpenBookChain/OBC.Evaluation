namespace OBC.Evaluation.Core;

/// <summary>
/// How to start one agent. In args, {promptFile} is a file holding the prompt, {prompt} the prompt itself and
/// {workDir} the run's empty working folder.
/// </summary>
public sealed class AgentCommand
{
    public string Command { get; init; } = string.Empty;
    public List<string> Args { get; init; } = new();

    /// <summary>How the prompt reaches the agent: stdin, arg (via {prompt}) or file (via {promptFile}).</summary>
    public string Prompt { get; init; } = "stdin";

    public string Output { get; init; } = "text";

    public Dictionary<string, string> Env { get; init; } = new();
    public string? McpConfigJson { get; init; }
    public List<string> AllowSkills { get; init; } = new();
}
