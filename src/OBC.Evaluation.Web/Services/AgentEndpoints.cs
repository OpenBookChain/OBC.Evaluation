using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OBC.Evaluation.Core;
using OBC.Evaluation.Web.Data;

namespace OBC.Evaluation.Web.Services;

/// <summary>
/// The agent API used by the runner. It hands out one run at a time and takes back the reply and its measures.
/// It needs the key in Evaluation:AgentApiKey; without one it is switched off.
/// </summary>
public static class AgentEndpoints
{
    public static void MapAgentApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/agent").AddEndpointFilter(async (context, next) =>
        {
            var expected = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>()["Evaluation:AgentApiKey"];
            if (string.IsNullOrWhiteSpace(expected))
                return Results.Problem("The agent API is off. Set Evaluation:AgentApiKey to turn it on.", statusCode: 503);
            var given = context.HttpContext.Request.Headers[AgentApi.KeyHeader].ToString();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected)))
                return Results.Unauthorized();
            return await next(context);
        });

        // The next run for an agent model, opened as an attempt. 204 when nothing can run.
        api.MapPost("/next", async (int model, int? size, string? setup, EvalService eval, IDbContextFactory<EvalDb> dbs) =>
        {
            await using var db = await dbs.CreateDbContextAsync();
            var profile = await db.Models.FindAsync(model);
            if (profile == null)
                return Results.NotFound($"No model {model}.");
            if (profile.Mode != Modes.Agent)
                return Results.BadRequest($"Model {model} is run manually; set its mode to agent to use the runner.");

            var next = await eval.NextAsync(model, size, setup, includeInProgress: false);
            if (next == null)
                return Results.NoContent();
            var attempt = await eval.StartAsync(next.Run.Id, Modes.Agent);
            AgentDataFile? file = null;
            if (next.Run.Setup == "csv")
            {
                var dataset = eval.Bundle.Datasets.First(d => d.Size == next.Task.Size);
                file = new AgentDataFile(dataset.Folder, next.Task.DataFile, dataset.Files[next.Task.DataFile]);
            }
            return Results.Ok(new AgentRun(attempt.Id, next.Run.Id, next.Task.Id, next.Task.Kind, next.Task.Size, next.Run.Setup,
                attempt.Prompt, attempt.PromptSha256, file));
        });

        api.MapPost("/attempts/{id:int}/submit", async (int id, AgentSubmit body, EvalService eval) =>
        {
            var check = await eval.SubmitAsync(id, body.Reply, a =>
            {
                a.DurationSeconds = body.DurationSeconds;
                a.InputTokens = body.Usage?.InputTokens;
                a.OutputTokens = body.Usage?.OutputTokens;
                a.CacheReadTokens = body.Usage?.CacheReadTokens;
                a.CacheCreationTokens = body.Usage?.CacheCreationTokens;
                a.CostUsd = body.Usage?.CostUsd;
                a.NumTurns = body.NumTurns;
                a.ToolCalls = body.ToolCalls;
                a.AgentCommand = body.AgentCommand;
                a.RawOutput = body.RawOutput;
            });
            return Results.Ok(new AgentSubmitResult(check.Status, check.Messages));
        });

        api.MapPost("/attempts/{id:int}/abandon", async (int id, AgentAbandon body, EvalService eval) =>
        {
            await eval.AbandonAsync(id, body.Reason, body.RawOutput);
            return Results.Ok();
        });
    }
}
