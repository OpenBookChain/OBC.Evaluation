using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace OBC.Evaluation.Core;

public sealed record AgentUsage(long? InputTokens, long? OutputTokens, long? CacheReadTokens, long? CacheCreationTokens, decimal? CostUsd);

/// <summary>What the agent says it ran with: the model, the skills and MCP servers it loaded, and how many connector (mcp__) tools it called. Only some tools report it.</summary>
public sealed record AgentContext(string? Model, IReadOnlyList<string> Skills, IReadOnlyList<string> McpServers, int? PremiumRequests, int? DataToolCalls = null)
{
    /// <summary>MCP servers the tool reported as unusable at the start of the run, with their status, such as "plain (needs-auth)".</summary>
    public IReadOnlyList<string> DownMcpServers { get; init; } = [];
}

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

    /// <summary>Where a run's MCP config is written, next to its prompt file, so the agent's working folder stays empty.</summary>
    public static string McpConfigFile(string promptFile) => promptFile.EndsWith(".prompt.md") ? promptFile[..^".prompt.md".Length] + ".mcp-config.json" : promptFile + ".mcp-config.json";

    public async Task<AgentResult> RunAsync(AgentCommand agent, string prompt, string workDir, string promptFile, TimeSpan timeout, CancellationToken cancel, Action<string>? onLine = null)
    {
        var args = agent.Args.Select(a => a.Replace("{promptFile}", promptFile).Replace("{workDir}", workDir)
            .Replace("{mcpConfigFile}", McpConfigFile(promptFile)).Replace("{prompt}", prompt)).ToList();
        var (fileName, prefixArgs) = ResolveCommand(agent.Command);
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        AddPersistentWindowsPath(start);
        foreach (var a in prefixArgs)
            start.ArgumentList.Add(a);
        foreach (var a in args)
            start.ArgumentList.Add(a);
        foreach (var (key, value) in agent.Env)
            start.Environment[key] = Environment.ExpandEnvironmentVariables(value);
        var commandLine = agent.Command + " " + string.Join(" ", agent.Args.Select(a => a.Length == 0 || a.Contains(' ') ? $"\"{a}\"" : a));

        var clock = Stopwatch.StartNew();
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start {fileName}.");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            var message = $"Couldn't start '{fileName}' for CLI '{agent.Command}'. The runner also checks the globally installed Codex package and persistent Windows User and Machine PATH. {ex.Message}";
            return new AgentResult(false, null, message, clock.Elapsed.TotalSeconds, null, null, commandLine, message);
        }
        using (process)
        {
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
                "codex-jsonl" => AgentOutput.ReadCodexJsonl(output, seconds, commandLine, raw),
                "text" => new AgentResult(true, output.Trim(), null, seconds, null, null, commandLine, raw),
                _ => throw new InvalidDataException($"Unknown output format '{agent.Output}'."),
            };
        }
    }

    private static (string FileName, IReadOnlyList<string> PrefixArgs) ResolveCommand(string command)
    {
        if (!OperatingSystem.IsWindows())
            return (command, []);
        if (!string.Equals(command, "codex", StringComparison.OrdinalIgnoreCase))
            return ResolveWindowsShim(command);

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var codexScript = Path.Combine(appData, "npm", "node_modules", "@openai", "codex", "bin", "codex.js");
        var node = Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files", "nodejs", "node.exe");
        if (File.Exists(codexScript) && File.Exists(node))
            return (node, [codexScript]);

        var profile = Environment.GetEnvironmentVariable("USERPROFILE");
        var codexBin = profile == null ? null : Path.Combine(profile, "AppData", "Local", "OpenAI", "Codex", "bin");
        var installedExe = codexBin != null && Directory.Exists(codexBin)
            ? Directory.EnumerateDirectories(codexBin)
                .Select(directory => Path.Combine(directory, "codex.exe"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
        return (installedExe ?? command, []);
    }

    /// <summary>npm installs CLIs as .cmd shims, which CreateProcess won't find by bare name; find the real .exe the shim runs, or run the shim through cmd.exe.</summary>
    private static (string FileName, IReadOnlyList<string> PrefixArgs) ResolveWindowsShim(string command)
    {
        if (Path.HasExtension(command))
            return (command, []);

        var dirs = new[]
            {
                Environment.GetEnvironmentVariable("PATH"),
                Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User),
                Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine),
            }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .SelectMany(p => p!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(Environment.ExpandEnvironmentVariables)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in dirs)
        {
            try
            {
                var exe = Path.Combine(dir, command + ".exe");
                if (File.Exists(exe))
                    return (exe, []);
                var cmd = Path.Combine(dir, command + ".cmd");
                if (!File.Exists(cmd))
                    continue;
                var target = Regex.Match(File.ReadAllText(cmd), "\"%dp0%\\\\([^\"]+\\.exe)\"", RegexOptions.IgnoreCase);
                if (target.Success && File.Exists(Path.Combine(dir, target.Groups[1].Value)))
                    return (Path.Combine(dir, target.Groups[1].Value), []);
                return ("cmd.exe", ["/c", cmd]);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
            }
        }
        return (command, []);
    }

    private static void AddPersistentWindowsPath(ProcessStartInfo start)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var paths = new[]
            {
                start.Environment["PATH"],
                Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User),
                Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine),
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .SelectMany(path => path!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(Environment.ExpandEnvironmentVariables)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        start.Environment["PATH"] = string.Join(Path.PathSeparator, paths);
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
