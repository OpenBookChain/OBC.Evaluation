using System.Text.Json;

namespace OBC.Evaluation.Runner;

/// <summary>runner.json: which model to run, where the app is, and how to start a fresh agent for each setup.</summary>
public sealed class RunnerConfig
{
    public string Api { get; init; } = "http://localhost:5234";

    /// <summary>The app's Evaluation:AgentApiKey. Leave empty and set OBC_EVAL_API_KEY instead, to keep it out of the file.</summary>
    public string? ApiKey { get; init; }

    /// <summary>The model's id on the Models page. It must be in agent mode.</summary>
    public int Model { get; init; }

    public int? Size { get; init; }
    public string? Setup { get; init; }

    /// <summary>Stop after this many runs; 0 runs until nothing is left.</summary>
    public int MaxRuns { get; init; }

    public int TimeoutMinutes { get; init; } = 30;

    /// <summary>OBC.SimulationDataGenerator/evaluation/data, for the CSV setup.</summary>
    public string DataRoot { get; init; } = string.Empty;

    /// <summary>Where each run's empty working folder is made. Defaults to the system temp folder.</summary>
    public string? WorkRoot { get; init; }

    public bool KeepWorkDirs { get; init; }

    public Dictionary<string, AgentCommand> Setups { get; init; } = new();

    public static RunnerConfig Load(string path)
    {
        var config = JsonSerializer.Deserialize<RunnerConfig>(File.ReadAllText(path),
                         new JsonSerializerOptions(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                     ?? throw new InvalidDataException($"{path} is empty.");
        if (config.Model == 0)
            throw new InvalidDataException($"{path}: set 'model' to the model's id from the Models page.");
        return config;
    }
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

    /// <summary>How to read the agent's output: claude-json (claude -p --output-format json) or text (stdout is the reply).</summary>
    public string Output { get; init; } = "text";

    public Dictionary<string, string> Env { get; init; } = new();
}
