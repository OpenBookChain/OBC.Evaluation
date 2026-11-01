using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OBC.Evaluation.Core;

namespace OBC.Evaluation.Runner;

public sealed record AgentResult(bool Ok, string? Reply, string? Error, double Seconds, AgentUsage? Usage, int? NumTurns, string CommandLine, string RawOutput);

/// <summary>Starts one agent process for one run and reads what it replied.</summary>
public static class AgentProcess
{
    public static async Task<AgentResult> RunAsync(AgentCommand agent, string prompt, string workDir, string promptFile, TimeSpan timeout)
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
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (agent.Prompt == "stdin")
            await process.StandardInput.WriteAsync(prompt);
        process.StandardInput.Close();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return new AgentResult(false, null, $"Timed out after {timeout.TotalMinutes:0} minutes.", clock.Elapsed.TotalSeconds, null, null, commandLine,
                await stdout + "\n--- stderr ---\n" + await stderr);
        }

        var output = await stdout;
        var raw = output + (await stderr is { Length: > 0 } err ? "\n--- stderr ---\n" + err : "");
        if (process.ExitCode != 0)
            return new AgentResult(false, null, $"{agent.Command} exited with code {process.ExitCode}.", clock.Elapsed.TotalSeconds, null, null, commandLine, raw);

        return agent.Output switch
        {
            "claude-json" => ReadClaudeJson(output, clock.Elapsed.TotalSeconds, commandLine, raw),
            "text" => new AgentResult(true, output.Trim(), null, clock.Elapsed.TotalSeconds, null, null, commandLine, raw),
            _ => throw new InvalidDataException($"Unknown output format '{agent.Output}'. Use claude-json or text."),
        };
    }

    /// <summary>claude -p --output-format json prints one object: the final message in 'result', plus usage, cost and turns.</summary>
    public static AgentResult ReadClaudeJson(string output, double seconds, string commandLine, string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            if (root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True)
                return new AgentResult(false, null, "The agent reported an error: " + Text(root, "result"), seconds, null, null, commandLine, raw);

            AgentUsage? usage = null;
            if (root.TryGetProperty("usage", out var u))
                usage = new AgentUsage(Long(u, "input_tokens"), Long(u, "output_tokens"), Long(u, "cache_read_input_tokens"),
                    Long(u, "cache_creation_input_tokens"),
                    root.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number ? cost.GetDecimal() : null);
            int? turns = root.TryGetProperty("num_turns", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : null;
            var reply = Text(root, "result");
            return reply == null
                ? new AgentResult(false, null, "The output has no 'result'.", seconds, usage, turns, commandLine, raw)
                : new AgentResult(true, reply, null, seconds, usage, turns, commandLine, raw);
        }
        catch (JsonException ex)
        {
            return new AgentResult(false, null, $"The output isn't JSON: {ex.Message}", seconds, null, null, commandLine, raw);
        }
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
}
