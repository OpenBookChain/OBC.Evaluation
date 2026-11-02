using Microsoft.EntityFrameworkCore;
using OBC.Evaluation.Core;
using OBC.Evaluation.Web.Components;
using OBC.Evaluation.Web.Data;
using OBC.Evaluation.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Paths in settings are relative to the repo root, so the same settings work from any folder.
var repoRoot = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", ".."));
string RepoPath(string key, string fallback) => Path.GetFullPath(Path.Combine(repoRoot, builder.Configuration[key] ?? fallback));

var bundle = Bundle.Load(RepoPath("Evaluation:BundlePath", "bundle"));
var dbPath = RepoPath("Evaluation:DatabasePath", "state/evaluation.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

builder.Services.AddSingleton(bundle);
builder.Services.AddSingleton(new DataFiles(RepoPath("Evaluation:DataRoot", "../OBC.SimulationDataGenerator/evaluation/data")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContextFactory<EvalDb>(o => o.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddScoped<EvalService>();
builder.Services.AddScoped<ExportService>();
builder.Services.AddScoped<ImportService>();
builder.Services.AddSingleton(new AgentSettings(RepoPath("Evaluation:AgentsConfig", "agents.json")));
builder.Services.AddSingleton<IAgentLauncher, AgentProcess>();
builder.Services.AddSingleton<AgentRunService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentRunService>());

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<EvalDb>>().CreateDbContextAsync();
    await db.Database.MigrateAsync();

    // A rebuilt bundle may add tasks; give every model the runs it is missing
    var eval = scope.ServiceProvider.GetRequiredService<EvalService>();
    foreach (var id in await db.Models.Select(m => m.Id).ToListAsync())
        await eval.EnsureRunsAsync(id);
}

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapGet("/export.zip", async (ExportService export, TimeProvider clock) =>
    Results.File(await export.ExportAsync(), "application/zip", $"obc-evaluation-evidence-{clock.GetUtcNow():yyyyMMdd-HHmm}.zip"));
app.MapGet("/data/{size:int}/{file}", (int size, string file, Bundle b, DataFiles data) =>
{
    // Only the CSVs the bundle names, so the answer files beside them are never served
    var dataset = b.Datasets.FirstOrDefault(d => d.Size == size);
    if (dataset == null || !dataset.Files.ContainsKey(file) || !file.EndsWith(".csv", StringComparison.Ordinal))
        return Results.NotFound();
    var path = data.PathOf(dataset, file);
    return File.Exists(path) ? Results.File(path, "text/csv", file) : Results.NotFound();
});

app.Run();

public partial class Program;
