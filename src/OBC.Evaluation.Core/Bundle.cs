using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OBC.Evaluation.Core;

/// <summary>
/// The frozen inputs of the evaluation (bundle/bundle.json), built by tools/build_bundle.py. Every run is made
/// from it, and every attempt records its SHA-256, so the export shows exactly what each model was given.
/// </summary>
public sealed class Bundle
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public int Schema { get; init; }
    public string Name { get; init; } = string.Empty;
    public List<string> Setups { get; init; } = new();
    public List<BundleDataset> Datasets { get; init; } = new();
    public List<BundleSkill> Skills { get; init; } = new();
    public string QaSkill { get; init; } = string.Empty;
    public List<EvalTask> Tasks { get; init; } = new();
    public List<ChecklistStep> Checklist { get; init; } = new();
    public BundleAnswerKey AnswerKey { get; init; } = new();

    /// <summary>SHA-256 of bundle.json as read from disk.</summary>
    [JsonIgnore] public string Sha256 { get; private set; } = string.Empty;

    /// <summary>The bundle folder, for the skill texts and the sealed answer key.</summary>
    [JsonIgnore] public string Folder { get; private set; } = string.Empty;

    [JsonIgnore] public IReadOnlyDictionary<string, string> SkillTexts { get; private set; } = new Dictionary<string, string>();

    public EvalTask Task(string id) => Tasks.First(t => t.Id == id);

    public BundleSkill Skill(string name) => Skills.First(s => s.Name == name);

    /// <summary>Reads bundle.json and every skill, and refuses a bundle whose files don't match their recorded hashes.</summary>
    public static Bundle Load(string folder)
    {
        var bytes = File.ReadAllBytes(Path.Combine(folder, "bundle.json"));
        var bundle = JsonSerializer.Deserialize<Bundle>(bytes, JsonOptions)
                     ?? throw new InvalidDataException("bundle.json is empty.");
        if (bundle.Schema != 1)
            throw new InvalidDataException($"bundle.json has schema {bundle.Schema}; this app reads schema 1.");

        var texts = new Dictionary<string, string>();
        foreach (var skill in bundle.Skills)
        {
            var skillBytes = File.ReadAllBytes(Path.Combine(folder, skill.File));
            if (Hashing.Sha256(skillBytes) != skill.Sha256)
                throw new InvalidDataException($"{skill.File} doesn't match the SHA-256 in bundle.json. Rebuild the bundle.");
            texts[skill.Name] = Encoding.UTF8.GetString(skillBytes);
        }

        var keyPath = Path.Combine(folder, bundle.AnswerKey.File);
        if (File.Exists(keyPath) && Hashing.Sha256(File.ReadAllBytes(keyPath)) != bundle.AnswerKey.Sha256)
            throw new InvalidDataException($"{bundle.AnswerKey.File} doesn't match the SHA-256 in bundle.json.");

        bundle.Sha256 = Hashing.Sha256(bytes);
        bundle.Folder = Path.GetFullPath(folder);
        bundle.SkillTexts = texts;
        return bundle;
    }
}

public sealed class BundleDataset
{
    public int Size { get; init; }
    public string Folder { get; init; } = string.Empty;
    public Dictionary<string, string> Files { get; init; } = new();
}

public sealed class BundleSkill
{
    public string Name { get; init; } = string.Empty;
    public string File { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
}

public sealed class BundleAnswerKey
{
    public string File { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
}

public static class TaskKinds
{
    public const string Qa = "qa";
    public const string Prepare = "prepare";
    public const string Audit = "audit";
    public const string Reaudit = "reaudit";
    public const string PlantedAudit = "planted-audit";

    public static bool IsAudit(string kind) => kind is Audit or Reaudit or PlantedAudit;
}

/// <summary>One question or report request. A run is a task in one setup for one model.</summary>
public sealed class EvalTask
{
    public string Id { get; init; } = string.Empty;
    public string Family { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Phase { get; init; } = string.Empty;
    public int Size { get; init; }
    public string Template { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Request { get; init; } = string.Empty;
    public List<string> Setups { get; init; } = new();
    public string DataFile { get; init; } = string.Empty;

    /// <summary>Held-out instances are kept for reruns and aren't in the run matrix.</summary>
    public bool HeldOut { get; init; }

    /// <summary>The preparer task whose saved output an audit reviews.</summary>
    public string? DependsOn { get; init; }

    /// <summary>For a planted-error audit: the errors to plant in a copy of the preparer's output.</summary>
    public JsonElement? PlantSpec { get; init; }

    public string SkillFor(string setup, string qaSkill) => Kind switch
    {
        TaskKinds.Qa => qaSkill,
        TaskKinds.Prepare => $"prepare-{setup}",
        _ => $"audit-{setup}"
    };
}

public sealed class ChecklistStep
{
    public string Id { get; init; } = string.Empty;
    public int Size { get; init; }
    public string Step { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Details { get; init; } = string.Empty;
}

public static class Hashing
{
    public static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static string Sha256(string text) => Sha256(Encoding.UTF8.GetBytes(text));
}
