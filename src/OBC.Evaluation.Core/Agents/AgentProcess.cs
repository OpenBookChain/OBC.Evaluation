using System.Diagnostics;
using System.Text;

namespace OBC.Evaluation.Core;

public sealed record AgentUsage(long? InputTokens, long? OutputTokens, long? CacheReadTokens, long? CacheCreationTokens, decimal? CostUsd);

/// <summary>What the agent says it ran with: the model, the skills and MCP servers it loaded, and how many connector (mcp__) tools it called. Only some tools report it.</summary>
public sealed record AgentContext(string? Model, IReadOnlyList<string> Skills, IReadOnlyList<string> McpServers, int? PremiumRequests, int? DataToolCalls = null);

public sealed record AgentResult(
    bool Ok, string? Reply, string? Error, double Seconds, AgentUsage? Usage, int? NumTurns, string CommandLine, string RawOutput,
    AgentContext? Context = null, int? ToolCalls = null);

/// <summary>Starts one agent process. A seam so tests can answer without a real CLI.</summary>
public interface IAgentLauncher
{
    /// <summary>onLine gets each line of stdout as it arrives; the lines are also appended to <see cref="OutputFile"/> so a stalled or killed run leaves a trace.</summary>
    Task<AgentResult> RunAsync(AgentCommand agent, string prompt, string workDir, string promptFile, TimeSpan timeout, CancellationToken cancel, Action<string>? onLine = null);
}

/// <summary>Starts one agent process for one run and reads what it replied.</summary>
public sealed class AgentProcess : IAgentLauncher
{
    /// <summary>Where a run's stdout is written while it runs, next to its prompt file.</summary>
    public static string OutputFile(string promptFile) => promptFile.EndsWith(".prompt.md") ? promptFile[..^".prompt.md".Length] + ".output.jsonl" : promptFile + ".output.jsonl";

    public async Task<AgentResult> RunAsync(AgentCommand agent, string prompt, string workDir, string promptFile, TimeSpan timeout, CancellationToken cancel, Action<string>? onLine = null)
    {
        var args = agent.Args.Select(a => a.Replace("{promptFile}", promptFile).Replace("{workDir}", workDir).Replace("{prompt}", prompt)).ToList();
        var start = new ProcessStartInfo(agent.Command)
        {
            WorkingDirectory = workDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
            start.ArgumentList.Add(a);
        foreach (var (key, value) in agent.Env)
            start.Environment[key] = Environment.ExpandEnvironmentVariables(value);
        var commandLine = agent.Command + " " + string.Join(" ", agent.Args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

        var clock = Stopwatch.StartNew();
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start {agent.Command}.");
        var stdout = ReadLinesAsync(process.StandardOutput, OutputFile(promptFile), onLine);
        var stderr = process.StandardError.ReadToEndAsync();
        if (agent.Prompt == "stdin")
            await process.StandardInput.WriteAsync(prompt);
        process.StandardInput.Close();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            var why = cancel.IsCancellationRequested ? "Stopped by the user." : $"Timed out after {timeout.TotalMinutes:0} minutes.";
            return new AgentResult(false, null, why, clock.Elapsed.TotalSeconds, null, null, commandLine, await stdout + "\n--- stderr ---\n" + await stderr);
        }

        var output = await stdout;
        var raw = output + (await stderr is { Length: > 0 } err ? "\n--- stderr ---\n" + err : "");
        var seconds = clock.Elapsed.TotalSeconds;
        if (process.ExitCode != 0)
            return new AgentResult(false, null, ExitMessage(agent, process.ExitCode, output, await stderr, seconds, commandLine), seconds, null, null, commandLine, raw);

        return agent.Output switch
        {
            "claude-json" => AgentOutput.ReadClaudeJson(output, seconds, commandLine, raw),
            "claude-stream-json" => AgentOutput.ReadClaudeStream(output, seconds, commandLine, raw),
            "copilot-jsonl" => AgentOutput.ReadCopilotJsonl(output, seconds, commandLine, raw),
            "text" => new AgentResult(true, output.Trim(), null, seconds, null, null, commandLine, raw),
            _ => throw new InvalidDataException($"Unknown output format '{agent.Output}'. Use claude-json, copilot-jsonl or text."),
        };
    }

    private static async Task<string> ReadLinesAsync(StreamReader reader, string file, Action<string>? onLine)
    {
        var all = new StringBuilder();
        await using var writer = new StreamWriter(file, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        while (await reader.ReadLineAsync() is { } line)
        {
            all.AppendLine(line);
            await writer.WriteLineAsync(line);
            onLine?.Invoke(line);
        }
        return all.ToString();
    }

    /// <summary>"exited with code 1" says nothing; add the tool's own reason when its output has one.</summary>
    public static string ExitMessage(AgentCommand agent, int exitCode, string output, string stderr, double seconds, string commandLine)
    {
        var message = $"{Path.GetFileName(agent.Command)} exited with code {exitCode}.";
        var claudeJson = agent.Output == "claude-stream-json" ? AgentOutput.LastResultLine(output) : agent.Output == "claude-json" ? output : null;
        if (!string.IsNullOrWhiteSpace(claudeJson) && AgentOutput.ReadClaudeJson(claudeJson, seconds, commandLine, claudeJson).Error is { } reason)
            message += " " + reason.Replace("The agent reported an error: ", "");
        else if (stderr.Trim() is { Length: > 0 } text)
            message += " " + text.Split('\n')[0].Trim();
        return message;
    }
}
