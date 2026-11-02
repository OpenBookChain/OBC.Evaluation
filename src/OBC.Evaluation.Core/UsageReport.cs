using System.Text.Json;

namespace OBC.Evaluation.Core;

/// <summary>What a model says it used for a run. It is the model's own estimate, not a measurement.</summary>
public sealed record UsageReport(long? InputTokens, long? OutputTokens, double? Seconds)
{
    public const string BlockName = "USAGE";
    public const string QaField = "usage_report";

    private const string Shape = "{\"input_tokens\": <number or null>, \"output_tokens\": <number or null>, \"seconds\": <number or null>}";
    private const string Honesty = "Give your best estimate of the tokens you read and wrote in this conversation and the seconds it took. "
        + "Use null for any figure you cannot estimate; do not guess.";

    /// <summary>The line added to every prompt, in the shape of that task kind's reply.</summary>
    public static string Instruction(string kind) => TaskKinds.IsAudit(kind) || kind == TaskKinds.Prepare
        ? $"After the other blocks, add a block <<<{BlockName}>>> {Shape} <<<END {BlockName}>>>. {Honesty}\n"
        : $"In the JSON object, add a field \"{QaField}\": {Shape}. {Honesty}\n";

    public static UsageReport? TryParse(string kind, string reply)
    {
        string? json;
        if (kind == TaskKinds.Qa)
        {
            var start = reply.IndexOf('{');
            var end = reply.LastIndexOf('}');
            json = start >= 0 && end > start ? reply[start..(end + 1)] : null;
        }
        else
            json = Blocks.Parse(reply).GetValueOrDefault(BlockName);
        if (json == null)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (kind == TaskKinds.Qa && !root.TryGetProperty(QaField, out root))
                return null;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            var report = new UsageReport(Long(root, "input_tokens"), Long(root, "output_tokens"), Double(root, "seconds"));
            return report.InputTokens == null && report.OutputTokens == null && report.Seconds == null ? null : report;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static double? Double(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
