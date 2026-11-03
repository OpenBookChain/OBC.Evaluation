using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using OBC.Evaluation.Core;
using OBC.Evaluation.Web.Data;

namespace OBC.Evaluation.Web.Services;

public sealed record AgentRunRequest(int ModelId, int? Size, string? Setup, int MaxRuns, decimal? MaxCostUsd);

public enum AgentServiceState { Idle, Running, Pausing, Stopping }

public sealed record AgentStatus(AgentServiceState State, string? Current, int Done, int Failed, decimal CostUsd, DateTime? StartedAt);

public sealed record PreflightResult(string Setup, bool Ok, string Message, AgentContext? Context, double Seconds);

/// <summary>
/// Runs an agent model's runs one after another, from inside the app. Each run is a fresh agent process in an empty
/// folder, given the same prompt the wizard shows. It stops when nothing is ready, when asked, after repeated failures
/// or at a cost cap, and never carries one run's conversation into the next.
/// </summary>
public sealed class AgentRunService(
    IServiceScopeFactory scopes, IDbContextFactory<EvalDb> dbs, Bundle bundle, DataFiles data, IAgentLauncher launcher) : IHostedService
{
    public const int MaxFailuresInARow = 3;
    private static readonly JsonSerializerOptions ProfileJson = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan AgentTimeout = TimeSpan.FromMinutes(30);
    private const string Probe = "Reply with exactly the word OK and nothing else.";
    private const string DataProbe = "Find the database schema tool and call it once. Then reply with exactly the word OK and nothing else.";

    private enum Outcome { Done, Failed, Stopped, NothingLeft, Fatal }

    private readonly object gate = new();
    private readonly List<string> log = new();
    private CancellationTokenSource? stop;
    private bool pause;
    private AgentServiceState state = AgentServiceState.Idle;
    private string? current;
    private int done, failed;
    private decimal cost;
    private DateTime? startedAt;
    private string? progress;

    /// <summary>The loop of the current or last session; tests await it.</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    public event Action? Changed;

    public AgentStatus Status
    {
        get { lock (gate) return new AgentStatus(state, current, done, failed, cost, startedAt); }
    }

    /// <summary>What the current run is doing, from the stream so far: turns and the last tool call. Only tools that stream report it.</summary>
    public string? Progress
    {
        get { lock (gate) return progress; }
    }

    public IReadOnlyList<string> Log
    {
        get { lock (gate) return log.ToList(); }
    }

    /// <summary>Starts a session. Returns why it couldn't, or null once it is running.</summary>
    public async Task<string?> StartAsync(AgentRunRequest request)
    {
        lock (gate)
            if (state != AgentServiceState.Idle)
                return "A session is already running.";

        ModelProfile? model;
        await using (var db = await dbs.CreateDbContextAsync())
            model = await db.Models.AsNoTracking().FirstOrDefaultAsync(m => m.Id == request.ModelId);
        if (model == null || model.Mode != Modes.Agent)
            return "Choose a model that is in agent mode.";
        if (!TryGetProfile(model, out var config, out var error))
            return error;

        lock (gate)
        {
            if (state != AgentServiceState.Idle)
                return "A session is already running.";
            stop = new CancellationTokenSource();
            pause = false;
            state = AgentServiceState.Running;
            done = failed = 0;
            cost = 0;
            current = null;
            startedAt = DateTime.Now;
            log.Clear();
        }
        Say($"Started {model.Name} ({model.ModelVersion}) with {model.AgentCli} CLI.");
        Completion = Task.Run(() => LoopAsync(request, model, config!, stop!.Token));
        return null;
    }

    /// <summary>Finish the current run, then stop.</summary>
    public void Pause()
    {
        lock (gate)
            if (state == AgentServiceState.Running)
            {
                pause = true;
                state = AgentServiceState.Pausing;
            }
        Changed?.Invoke();
    }

    /// <summary>Kill the current run (it is abandoned with the reason) and stop.</summary>
    public void Stop()
    {
        CancellationTokenSource? source;
        lock (gate)
        {
            if (state == AgentServiceState.Idle)
                return;
            state = AgentServiceState.Stopping;
            source = stop;
        }
        source?.Cancel();
        Changed?.Invoke();
    }

    private async Task LoopAsync(AgentRunRequest request, ModelProfile model, AgentProfileConfig config, CancellationToken cancel)
    {
        var failuresInARow = 0;
        try
        {
            while ((request.MaxRuns == 0 || done < request.MaxRuns) && !cancel.IsCancellationRequested)
            {
                lock (gate)
                    if (pause)
                        break;
                if (request.MaxCostUsd is { } cap && cost >= cap)
                {
                    Say($"Cost cap of ${cap:0.##} reached (${cost:0.####} spent).");
                    break;
                }

                var outcome = await RunOneAsync(request, model, config, cancel);
                if (outcome is Outcome.NothingLeft or Outcome.Fatal or Outcome.Stopped)
                    break;
                failuresInARow = outcome == Outcome.Failed ? failuresInARow + 1 : 0;
                if (failuresInARow >= MaxFailuresInARow)
                {
                    Say($"Stopped after {MaxFailuresInARow} failed runs in a row. Check the agent's sign-in and configuration.");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Say($"Stopped by an error: {ex.Message}");
        }
        finally
        {
            lock (gate)
            {
                state = AgentServiceState.Idle;
                current = null;
                stop?.Dispose();
                stop = null;
            }
            Say($"Session ended: {done} saved, {failed} failed.");
        }
    }

    private async Task<Outcome> RunOneAsync(AgentRunRequest request, ModelProfile model, AgentProfileConfig config, CancellationToken cancel)
    {
        using var scope = scopes.CreateScope();
        var eval = scope.ServiceProvider.GetRequiredService<EvalService>();

        var next = await eval.NextAsync(request.ModelId, request.Size, request.Setup, includeInProgress: false);
        if (next == null)
        {
            Say("Nothing left to run for this model with these filters. Check the dashboard for what is blocking the rest.");
            return Outcome.NothingLeft;
        }
        if (!config.Setups.TryGetValue(next.Run.Setup, out var template))
        {
            Say($"Model profile has no CLI command for the {next.Run.Setup} setup.");
            return Outcome.Fatal;
        }

        var attempt = await eval.StartAsync(next.Run.Id, Modes.Agent);
        var label = $"{next.Task.Id} · {next.Run.Setup} · attempt {attempt.Id}";
        lock (gate)
            current = label;
        Changed?.Invoke();

        var workRoot = WorkRoot();
        var workDir = Path.Combine(workRoot, $"attempt-{attempt.Id}");
        var promptFile = Path.Combine(workRoot, $"attempt-{attempt.Id}.prompt.md");
        var mcpConfigFile = Path.Combine(workDir, "mcp-config.json");
        var command = ForModel(template, model, mcpConfigFile);
        try
        {
            Directory.CreateDirectory(workDir);
            if (command.McpConfigJson != null)
                await File.WriteAllTextAsync(mcpConfigFile, command.McpConfigJson, cancel);
            if (next.Run.Setup == "csv")
            {
                var dataset = bundle.Datasets.First(d => d.Size == next.Task.Size);
                if (data.Matches(dataset, next.Task.DataFile) != true)
                {
                    var why = $"{data.PathOf(dataset, next.Task.DataFile)} is missing or differs from the bundle.";
                    await eval.AbandonAsync(attempt.Id, why);
                    Say(why);
                    return Outcome.Fatal;
                }
                File.Copy(data.PathOf(dataset, next.Task.DataFile), Path.Combine(workDir, next.Task.DataFile));
            }
            await File.WriteAllTextAsync(promptFile, attempt.Prompt, cancel);

            var result = await launcher.RunAsync(command, attempt.Prompt, workDir, promptFile, AgentTimeout, cancel, Track(started: DateTime.Now));
            if (!result.Ok)
            {
                await eval.AbandonAsync(attempt.Id, result.Error!, result.RawOutput);
                lock (gate) failed++;
                Say($"{label}: failed. {result.Error}");
                return cancel.IsCancellationRequested ? Outcome.Stopped : Outcome.Failed;
            }

            var unexpected = (result.Context?.Skills ?? []).Where(s => !command.AllowSkills.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
            if (unexpected.Count > 0)
            {
                var why = $"The agent loaded skills the study did not give it: {string.Join(", ", unexpected)}. Run it from a clean profile.";
                await eval.AbandonAsync(attempt.Id, why, result.RawOutput);
                lock (gate) failed++;
                Say($"{label}: {why}");
                return Outcome.Fatal;
            }

            if (next.Run.Setup != "csv" && result.Context?.DataToolCalls == 0)
            {
                var why = "The agent never called a data tool, so its answer didn't come from the data source. Not saved as a result.";
                await eval.AbandonAsync(attempt.Id, why, result.RawOutput);
                lock (gate) failed++;
                Say($"{label}: {why}");
                return Outcome.Failed;
            }

            var notes = Notes(result.Context, model);
            var check = await eval.SubmitAsync(attempt.Id, result.Reply!, a =>
            {
                a.DurationSeconds = result.Seconds;
                a.InputTokens = result.Usage?.InputTokens;
                a.OutputTokens = result.Usage?.OutputTokens;
                a.CacheReadTokens = result.Usage?.CacheReadTokens;
                a.CacheCreationTokens = result.Usage?.CacheCreationTokens;
                a.CostUsd = result.Usage?.CostUsd;
                a.NumTurns = result.NumTurns;
                a.ToolCalls = result.ToolCalls;
                a.AgentCommand = result.CommandLine;
                a.RawOutput = result.RawOutput;
                a.Notes = notes;
            });
            lock (gate)
            {
                done++;
                cost += result.Usage?.CostUsd ?? 0;
            }
            Say($"{label}: {check.Status} in {result.Seconds:0}s" + (check.Messages.Count > 0 ? $" ({string.Join("; ", check.Messages)})" : "")
                + (notes != null && notes.Contains("MODEL MISMATCH") ? " — " + notes : ""));
            return Outcome.Done;
        }
        finally
        {
            lock (gate)
            {
                current = null;
                progress = null;
            }
            Changed?.Invoke();
            TryDelete(() => Directory.Delete(workDir, recursive: true));
            TryDelete(() => File.Delete(promptFile));
            TryDelete(() => File.Delete(AgentProcess.OutputFile(promptFile)));
        }
    }

    /// <summary>A line reader that keeps <see cref="Progress"/> current: how long the run has gone, its turns and the last tool it called.</summary>
    private Action<string> Track(DateTime started)
    {
        int turns = 0, tools = 0;
        string? last = null;
        lock (gate) progress = "starting";
        Changed?.Invoke();
        return line =>
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                var root = doc.RootElement;
                var eventType = root.TryGetProperty("type", out var eventProp) ? eventProp.GetString() : null;
                if (eventType == "item.started" && root.TryGetProperty("item", out var item)
                    && Text(item, "type") is "mcp_tool_call" or "command_execution")
                {
                    turns++;
                    tools++;
                    last = Text(item, "name") ?? Text(item, "type");
                    lock (gate) progress = $"last event at {(DateTime.Now - started).TotalSeconds:0}s · {turns} events · {tools} tool calls · last: {last}";
                    Changed?.Invoke();
                    return;
                }
                if (eventType == "turn.completed")
                {
                    turns++;
                    lock (gate) progress = $"last event at {(DateTime.Now - started).TotalSeconds:0}s · {turns} turns · {tools} tool calls";
                    Changed?.Invoke();
                    return;
                }
                if (eventType != "assistant" || !root.TryGetProperty("message", out var message)
                    || !message.TryGetProperty("content", out var content)) return;
                turns++;
                foreach (var block in content.EnumerateArray().Where(b => b.TryGetProperty("type", out var t) && t.GetString() == "tool_use"))
                {
                    tools++;
                    last = block.GetProperty("name").GetString()?.Split("__").Last();
                }
            }
            catch (System.Text.Json.JsonException) { return; }
            lock (gate)
                progress = $"last event at {(DateTime.Now - started).TotalSeconds:0}s · {turns} turns · {tools} tool calls" + (last != null ? $" · last: {last}" : "");
            Changed?.Invoke();
        };
    }

    private static string? Text(System.Text.Json.JsonElement element, string name) =>
        element.ValueKind == System.Text.Json.JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null;

    /// <summary>Runs a trivial prompt for each setup the model's tool has a command for, to check it starts and what it loads.</summary>
    public async Task<List<PreflightResult>> PreflightAsync(int modelId)
    {
        ModelProfile? model;
        await using (var db = await dbs.CreateDbContextAsync())
            model = await db.Models.AsNoTracking().FirstOrDefaultAsync(m => m.Id == modelId);
        if (model == null)
            return [new PreflightResult("profile", false, "Model profile was not found.", null, 0)];
        if (!TryGetProfile(model, out var config, out var error))
            return [new PreflightResult("profile", false, error!, null, 0)];

        var results = new List<PreflightResult>();
        foreach (var (setup, template) in config!.Setups.OrderBy(s => bundle.Setups.IndexOf(s.Key)))
        {
            var workDir = Path.Combine(WorkRoot(), $"preflight-{Guid.NewGuid():N}");
            var promptFile = workDir + ".prompt.md";
            var mcpConfigFile = Path.Combine(workDir, "mcp-config.json");
            var command = ForModel(template, model, mcpConfigFile);
            Directory.CreateDirectory(workDir);
            try
            {
                if (command.McpConfigJson != null)
                    await File.WriteAllTextAsync(mcpConfigFile, command.McpConfigJson);
                var probe = setup == "csv" ? Probe : DataProbe;
                await File.WriteAllTextAsync(promptFile, probe);
                var result = await launcher.RunAsync(command, probe, workDir, promptFile, TimeSpan.FromMinutes(5), CancellationToken.None);
                TryDelete(() => File.Delete(promptFile));
                var skills = (result.Context?.Skills ?? []).Where(s => !command.AllowSkills.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
                var problems = new List<string>();
                if (!result.Ok)
                    problems.Add(result.Error!);
                if (result.Ok && setup != "csv" && result.Context?.DataToolCalls == 0)
                    problems.Add("never called the data tool, so the connector isn't reachable");
                if (skills.Count > 0)
                    problems.Add($"loads skills: {string.Join(", ", skills)}");
                if (result.Context?.Model is { } m && !string.Equals(m, model.ModelVersion, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"runs model '{m}', not '{model.ModelVersion}'");
                results.Add(new PreflightResult(setup, problems.Count == 0, problems.Count == 0 ? $"Replied \"{result.Reply?.Trim()}\"." : string.Join("; ", problems), result.Context, result.Seconds));
            }
            catch (Exception ex)
            {
                results.Add(new PreflightResult(setup, false, ex.Message, null, 0));
            }
            finally
            {
                TryDelete(() => Directory.Delete(workDir, recursive: true));
                TryDelete(() => File.Delete(promptFile));
                TryDelete(() => File.Delete(AgentProcess.OutputFile(promptFile)));
            }
        }
        return results;
    }

    /// <summary>The command with {model} replaced by the model's exact version, so one entry serves every model of a tool.</summary>
    public static AgentCommand ForModel(AgentCommand command, ModelProfile model, string? mcpConfigFile = null) => new()
    {
        Command = command.Command,
        Args = command.Args.Select(a => a.Replace("{model}", model.ModelVersion).Replace("{mcpConfigFile}", mcpConfigFile ?? "")).ToList(),
        Prompt = command.Prompt,
        Output = command.Output,
        Env = command.Env,
        McpConfigJson = command.McpConfigJson,
        AllowSkills = command.AllowSkills,
    };

    private static string? Notes(AgentContext? context, ModelProfile model)
    {
        if (context == null)
            return null;
        var parts = new List<string>();
        if (context.Model != null)
            parts.Add($"Model reported: {context.Model}.");
        if (context.Model != null && !string.Equals(context.Model, model.ModelVersion, StringComparison.OrdinalIgnoreCase))
            parts.Add($"MODEL MISMATCH: expected {model.ModelVersion}.");
        if (context.PremiumRequests != null)
            parts.Add($"Premium requests: {context.PremiumRequests}.");
        if (context.McpServers.Count > 0)
            parts.Add($"MCP servers: {string.Join(", ", context.McpServers)}.");
        return string.Join(" ", parts);
    }

    private static bool TryGetProfile(ModelProfile model, out AgentProfileConfig? config, out string? error)
    {
        try
        {
            config = JsonSerializer.Deserialize<AgentProfileConfig>(model.AgentConfigJson ?? "", ProfileJson);
            if (config == null || !string.Equals(config.Cli, model.AgentCli, StringComparison.OrdinalIgnoreCase))
            {
                error = $"Model {model.Id} has an invalid CLI profile. Choose its CLI again on the Models page to regenerate it.";
                return false;
            }
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            config = null;
            error = $"Model {model.Id} has invalid CLI profile JSON: {ex.Message}";
            return false;
        }
    }

    private static string WorkRoot()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "obc-eval-runs"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(Action delete)
    {
        try { delete(); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Say(string message)
    {
        lock (gate)
        {
            log.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            if (log.Count > 300)
                log.RemoveRange(0, log.Count - 300);
        }
        Changed?.Invoke();
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Stop();
        await Task.WhenAny(Completion, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));
    }
}
