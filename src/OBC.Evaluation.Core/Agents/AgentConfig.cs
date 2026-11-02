using System.Text.Json;

namespace OBC.Evaluation.Core;

/// <summary>
/// agents.json: how to start a fresh agent for each tool and setup. A model's App on the Models page names the tool
/// (for example "Claude Code" or "Copilot CLI"), so the runner knows which commands belong to it.
/// </summary>
public sealed class AgentsConfig
{
    public int TimeoutMinutes { get; init; } = 30;

    /// <summary>Where each run's empty working folder is made. Defaults to the system temp folder.</summary>
    public string? WorkRoot { get; init; }

    public bool KeepWorkDirs { get; init; }

    /// <summary>Tool name → setup (obc, plain, csv) → the command that starts the agent.</summary>
    public Dictionary<string, Dictionary<string, AgentCommand>> Agents { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public static AgentsConfig Load(string path)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        var config = JsonSerializer.Deserialize<AgentsConfig>(File.ReadAllText(path), options)
                     ?? throw new InvalidDataException($"{path} is empty.");
        return new AgentsConfig
        {
            TimeoutMinutes = config.TimeoutMinutes,
            WorkRoot = config.WorkRoot,
            KeepWorkDirs = config.KeepWorkDirs,
            Agents = new(config.Agents, StringComparer.OrdinalIgnoreCase),
        };
    }

    public AgentCommand? Find(string tool, string setup) =>
        Agents.TryGetValue(tool, out var setups) && setups.TryGetValue(setup, out var command) ? command : null;
}

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

    /// <summary>How to read the agent's output: claude-stream-json, claude-json, copilot-jsonl, or text (stdout is the reply).</summary>
    public string Output { get; init; } = "text";

    public Dictionary<string, string> Env { get; init; } = new();

    /// <summary>
    /// Skills the agent may load. A run whose agent reports any other skill is thrown away, because a skill the study
    /// did not give it would change the result. Only tools that report their skills (Copilot) are checked.
    /// </summary>
    public List<string> AllowSkills { get; init; } = new();
}
