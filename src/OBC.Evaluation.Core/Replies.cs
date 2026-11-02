using System.Text.Json;
using System.Text.RegularExpressions;

namespace OBC.Evaluation.Core;

/// <summary>The fixed output markers of the preparer and auditor skills.</summary>
public static partial class Blocks
{
    public const string Report = "REPORT";
    public const string WorkingPapers = "WORKING PAPERS";
    public const string ClaimsJson = "CLAIMS JSON";
    public const string AuditMemo = "AUDIT MEMO";
    public const string AuditorWorkingPapers = "AUDITOR WORKING PAPERS";
    public const string VerdictsJson = "VERDICTS JSON";

    public static readonly string[] Preparer = [Report, WorkingPapers, ClaimsJson];
    public static readonly string[] Auditor = [AuditMemo, AuditorWorkingPapers, VerdictsJson];

    public static string Wrap(string name, string body) => $"<<<{name}>>>\n{body.Trim()}\n<<<END {name}>>>";

    /// <summary>Every <c>&lt;&lt;&lt;NAME&gt;&gt;&gt; … &lt;&lt;&lt;END NAME&gt;&gt;&gt;</c> block, by name. The first of a repeated name wins.</summary>
    public static Dictionary<string, string> Parse(string text)
    {
        var blocks = new Dictionary<string, string>();
        foreach (Match m in BlockPattern().Matches(text.Replace("\r\n", "\n")))
            blocks.TryAdd(m.Groups[1].Value, m.Groups[2].Value.Trim());
        return blocks;
    }

    /// <summary>Text outside the blocks, ignoring whitespace and code fences around them.</summary>
    public static string Outside(string text)
    {
        var rest = BlockPattern().Replace(text.Replace("\r\n", "\n"), "");
        return FencePattern().Replace(rest, "").Trim();
    }

    [GeneratedRegex(@"<<<([A-Z ]+)>>>\s*(.*?)\s*<<<END \1>>>", RegexOptions.Singleline)]
    private static partial Regex BlockPattern();

    [GeneratedRegex(@"^\s*```[a-z]*\s*$", RegexOptions.Multiline)]
    private static partial Regex FencePattern();
}

public sealed record ReplyCheck(string Status, List<string> Messages, List<string> ReceiptIds)
{
    public const string Valid = "valid";
    public const string Issues = "issues";
    public const string Invalid = "invalid";
}

/// <summary>
/// Checks a reply's structure as soon as it is saved, so a broken paste or a reply in the wrong shape is caught
/// while the chat is still open. It never judges the answer: scoring stays sealed until the end.
/// </summary>
public static partial class ReplyValidator
{
    private static readonly string[] QaFields = ["answer_status", "answer_type", "final_answer", "method", "source_references", "confidence"];
    private static readonly string[] QaStatuses = ["answered", "partial", "cannot_answer"];
    private static readonly string[] Verdicts = ["supported", "unsupported", "no_evidence", "cannot_verify", "evidence_changed"];

    public static ReplyCheck Check(string kind, string setup, string reply)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(reply))
            return new ReplyCheck(ReplyCheck.Invalid, ["The reply is empty."], new());

        if (kind == TaskKinds.Qa)
            CheckQa(reply, errors, warnings);
        else
            CheckBlocks(reply, kind == TaskKinds.Prepare ? Blocks.Preparer : Blocks.Auditor,
                kind == TaskKinds.Prepare ? ("claims", (string?)null) : ("verdicts", "verdict"), errors, warnings);

        var receipts = setup == "obc" ? ReceiptPattern().Matches(reply).Select(m => m.Value).Distinct().ToList() : new List<string>();
        var status = errors.Count > 0 ? ReplyCheck.Invalid : warnings.Count > 0 ? ReplyCheck.Issues : ReplyCheck.Valid;
        return new ReplyCheck(status, errors.Concat(warnings).ToList(), receipts);
    }

    private static void CheckQa(string reply, List<string> errors, List<string> warnings)
    {
        var text = reply.Trim();
        var fenced = FencedJson().Match(text);
        if (fenced.Success)
        {
            warnings.Add("The JSON is wrapped in a code fence; the skill asks for bare JSON.");
            text = fenced.Groups[1].Value.Trim();
        }
        else if (!text.StartsWith('{'))
        {
            var start = text.IndexOf('{');
            if (start < 0)
            {
                errors.Add("The reply has no JSON object.");
                return;
            }
            warnings.Add("There is text before the JSON; the skill asks for the JSON only.");
            text = text[start..];
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add("The JSON is not an object.");
                return;
            }
            foreach (var field in QaFields.Where(f => !doc.RootElement.TryGetProperty(f, out _)))
                errors.Add($"The JSON has no '{field}'.");
            if (doc.RootElement.TryGetProperty("answer_status", out var status)
                && (status.ValueKind != JsonValueKind.String || !QaStatuses.Contains(status.GetString())))
                errors.Add($"answer_status must be one of: {string.Join(", ", QaStatuses)}.");
        }
        catch (JsonException ex)
        {
            errors.Add($"The JSON doesn't parse: {ex.Message}");
        }
    }

    private static void CheckBlocks(string reply, string[] names, (string Array, string? Field) json, List<string> errors, List<string> warnings)
    {
        var blocks = Blocks.Parse(reply);
        foreach (var name in names.Where(n => !blocks.ContainsKey(n)))
            errors.Add($"The <<<{name}>>> block is missing or not closed with <<<END {name}>>>.");
        if (Blocks.Outside(reply).Length > 0)
            warnings.Add("There is text outside the blocks; the skill asks for the blocks only.");

        var jsonName = names[^1];
        if (!blocks.TryGetValue(jsonName, out var body))
            return;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty(json.Array, out var items) || items.ValueKind != JsonValueKind.Array)
            {
                errors.Add($"{jsonName} has no '{json.Array}' array.");
                return;
            }
            if (json.Field != null)
                foreach (var item in items.EnumerateArray())
                    if (!item.TryGetProperty(json.Field, out var v) || !Verdicts.Contains(v.GetString()))
                        errors.Add($"A verdict is not one of: {string.Join(", ", Verdicts)}.");
        }
        catch (JsonException ex)
        {
            errors.Add($"{jsonName} doesn't parse: {ex.Message}");
        }
    }

    [GeneratedRegex(@"^```(?:json)?\s*\n(.*)\n```$", RegexOptions.Singleline)]
    private static partial Regex FencedJson();

    /// <summary>FinAI query receipts are 32 lowercase hex characters.</summary>
    [GeneratedRegex(@"\b[0-9a-f]{32}\b")]
    private static partial Regex ReceiptPattern();
}
