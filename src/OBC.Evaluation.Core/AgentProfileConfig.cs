namespace OBC.Evaluation.Core;

/// <summary>Per-model CLI and setup configuration stored with the model profile.</summary>
public sealed class AgentProfileConfig
{
    public string Cli { get; init; } = string.Empty;
    public Dictionary<string, AgentCommand> Setups { get; init; } = new();
}
