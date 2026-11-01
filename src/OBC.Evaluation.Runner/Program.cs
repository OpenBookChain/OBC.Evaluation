using System.Net;
using System.Net.Http.Json;
using OBC.Evaluation.Core;
using OBC.Evaluation.Runner;

// The agent runner: asks the evaluation app for the next run of an agent model, starts a fresh agent process
// for it in an empty folder with only that setup's data source, and posts the reply back. One process per run,
// so no run can see another's question or answer.
//
//   dotnet run --project src/OBC.Evaluation.Runner -- --config runner.json [--max 5]

var configPath = Arg("--config") ?? "runner.json";
var config = RunnerConfig.Load(configPath);
var maxRuns = int.TryParse(Arg("--max"), out var m) ? m : config.MaxRuns;
var apiKey = string.IsNullOrWhiteSpace(config.ApiKey) ? Environment.GetEnvironmentVariable("OBC_EVAL_API_KEY") : config.ApiKey;
if (string.IsNullOrWhiteSpace(apiKey))
    return Fail("No API key: set 'apiKey' in the config or OBC_EVAL_API_KEY.");

var workRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(config.WorkRoot ?? Path.Combine(Path.GetTempPath(), "obc-eval-runs")));
Directory.CreateDirectory(workRoot);
using var http = new HttpClient { BaseAddress = new Uri(config.Api), Timeout = TimeSpan.FromMinutes(2) };
http.DefaultRequestHeaders.Add(AgentApi.KeyHeader, apiKey);

var done = 0;
while (maxRuns == 0 || done < maxRuns)
{
    var query = $"api/agent/next?model={config.Model}"
                + (config.Size is int size ? $"&size={size}" : "")
                + (config.Setup is { } setup ? $"&setup={Uri.EscapeDataString(setup)}" : "");
    using var response = await http.PostAsync(query, null);
    if (response.StatusCode == HttpStatusCode.NoContent)
    {
        Console.WriteLine("Nothing left to run for this model with these filters.");
        break;
    }
    if (!response.IsSuccessStatusCode)
        return Fail($"The app refused the next run: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    var run = (await response.Content.ReadFromJsonAsync<AgentRun>())!;

    if (Hashing.Sha256(run.Prompt) != run.PromptSha256)
        return Fail($"Attempt {run.AttemptId}: the prompt doesn't match its SHA-256.");
    if (!config.Setups.TryGetValue(run.Setup, out var agent))
    {
        await Abandon(run.AttemptId, $"runner.json has no command for setup '{run.Setup}'.", null);
        return Fail($"runner.json has no command for setup '{run.Setup}'.");
    }

    Console.Write($"[{DateTime.Now:HH:mm:ss}] attempt {run.AttemptId}: {run.TaskId} · {run.Setup} … ");
    var workDir = Path.Combine(workRoot, $"attempt-{run.AttemptId}");
    var promptFile = Path.Combine(workRoot, $"attempt-{run.AttemptId}.prompt.md");
    Directory.CreateDirectory(workDir);
    try
    {
        if (run.DataFile is { } data)
        {
            var source = Path.Combine(config.DataRoot, data.Folder, data.Name);
            if (!File.Exists(source) || Hashing.Sha256(await File.ReadAllBytesAsync(source)) != data.Sha256)
            {
                await Abandon(run.AttemptId, $"{source} is missing or differs from the bundle.", null);
                return Fail($"{source} is missing or differs from the bundle.");
            }
            File.Copy(source, Path.Combine(workDir, data.Name));
        }
        await File.WriteAllTextAsync(promptFile, run.Prompt);

        var result = await AgentProcess.RunAsync(agent, run.Prompt, workDir, promptFile, TimeSpan.FromMinutes(config.TimeoutMinutes));
        if (!result.Ok)
        {
            await Abandon(run.AttemptId, result.Error!, result.RawOutput);
            Console.WriteLine($"failed: {result.Error}");
        }
        else
        {
            using var submit = await http.PostAsJsonAsync($"api/agent/attempts/{run.AttemptId}/submit",
                new AgentSubmit(result.Reply!, result.Seconds, result.Usage, result.NumTurns, null, result.CommandLine, result.RawOutput));
            submit.EnsureSuccessStatusCode();
            var saved = (await submit.Content.ReadFromJsonAsync<AgentSubmitResult>())!;
            Console.WriteLine($"{saved.Status} in {result.Seconds:0}s" + (saved.Messages.Count > 0 ? $" ({string.Join("; ", saved.Messages)})" : ""));
        }
    }
    finally
    {
        if (!config.KeepWorkDirs)
        {
            Directory.Delete(workDir, recursive: true);
            File.Delete(promptFile);
        }
    }
    done++;
}
Console.WriteLine($"{done} run(s) this session.");
return 0;

async Task Abandon(int attemptId, string reason, string? raw)
{
    using var r = await http.PostAsJsonAsync($"api/agent/attempts/{attemptId}/abandon", new AgentAbandon(reason, raw));
}

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
