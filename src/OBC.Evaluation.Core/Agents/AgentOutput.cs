using System.Text.Json;

namespace OBC.Evaluation.Core;

/// <summary>Readers for what each CLI prints. One per tool; each turns the output into an <see cref="AgentResult"/>.</summary>
public static class AgentOutput
{
    /// <summary>Reads Codex CLI's JSONL stream and returns its final assistant message and usage.</summary>
    public static AgentResult ReadCodexJsonl(string output, double seconds, string commandLine, string raw)
    {
        string? reply = null, failure = null;
        long? inputTokens = null, outputTokens = null, cacheReadTokens = null;
        var turns = 0;
        var toolIds = new HashSet<string>(StringComparer.Ordinal);
        var anonymousTools = 0;
        var dataCalls = 0;

        foreach (var line in Lines(output))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                var type = Text(root, "type");
                if (type == "item.completed" && root.TryGetProperty("item", out var item) && Text(item, "type") == "agent_message")
                    reply = Text(item, "text") ?? ReadItemText(item);
                else if (type == "item.started" && root.TryGetProperty("item", out var started))
                {
                    var itemType = Text(started, "type");
                    if (itemType is "mcp_tool_call" or "command_execution")
                    {
                        var id = Text(started, "id");
                        if (id == null) anonymousTools++;
                        else toolIds.Add(id);
                        if (itemType == "mcp_tool_call") dataCalls++;
                    }
                }
                else if (type == "turn.completed")
                {
                    turns++;
                    if (root.TryGetProperty("usage", out var usage))
                    {
                        inputTokens = Long(usage, "input_tokens") ?? inputTokens;
                        outputTokens = Long(usage, "output_tokens") ?? outputTokens;
                        cacheReadTokens = Long(usage, "cached_input_tokens") ?? Long(usage, "cache_read_input_tokens") ?? cacheReadTokens;
                    }
                }
                else if (type == "turn.failed")
                    failure = root.TryGetProperty("error", out var error) ? error.ToString() : "Codex reported a failed turn.";
            }
        }

        AgentUsage? usageResult = inputTokens is null && outputTokens is null && cacheReadTokens is null
            ? null : new AgentUsage(inputTokens, outputTokens, cacheReadTokens, null, null);
        var context = new AgentContext(null, [], [], null, dataCalls);
        var toolCalls = toolIds.Count + anonymousTools;
        if (failure != null)
            return new AgentResult(false, null, failure, seconds, usageResult, turns == 0 ? null : turns, commandLine, raw, context, toolCalls);
        return reply == null
            ? new AgentResult(false, null, "The Codex JSONL output has no completed assistant message.", seconds, usageResult, turns == 0 ? null : turns, commandLine, raw, context, toolCalls)
            : new AgentResult(true, reply, null, seconds, usageResult, turns == 0 ? null : turns, commandLine, raw, context, toolCalls);
    }

    private static string? ReadItemText(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return null;
        return string.Concat(content.EnumerateArray().Where(part => Text(part, "type") == "text").Select(part => Text(part, "text")));
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

    /// <summary>
    /// claude -p --output-format stream-json --verbose prints one event per line. Its init event says which model,
    /// skills and MCP servers the run had, and the closing result event has the same fields as the json format.
    /// </summary>
    public static AgentResult ReadClaudeStream(string output, double seconds, string commandLine, string raw)
    {
        string? model = null;
        var skills = new List<string>();
        var servers = new List<string>();
        var down = new List<string>();
        int toolCalls = 0, dataCalls = 0;
        foreach (var line in Lines(output))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (Text(root, "type") == "assistant" && root.TryGetProperty("message", out var message)
                    && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    var uses = content.EnumerateArray().Where(b => Text(b, "type") == "tool_use").ToList();
                    toolCalls += uses.Count;
                    dataCalls += uses.Count(b => Text(b, "name")?.StartsWith("mcp__") == true);
                }
                if (Text(root, "type") != "system" || Text(root, "subtype") != "init")
                    continue;
                model = Text(root, "model");
                if (root.TryGetProperty("skills", out var list) && list.ValueKind == JsonValueKind.Array)
                    skills.AddRange(list.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString()!));
                if (root.TryGetProperty("mcp_servers", out var mcp) && mcp.ValueKind == JsonValueKind.Array)
                {
                    servers.AddRange(mcp.EnumerateArray().Select(s => Text(s, "name")).OfType<string>());
                    down.AddRange(mcp.EnumerateArray().Where(s => Text(s, "status") is "needs-auth" or "failed").Select(s => $"{Text(s, "name")} ({Text(s, "status")})"));
                }
            }
            catch (JsonException) { }
        }

        var result = LastResultLine(output);
        if (result == null)
            return new AgentResult(false, null, "The output has no result event.", seconds, null, null, commandLine, raw, new AgentContext(model, skills, servers, null) { DownMcpServers = down });
        return ReadClaudeJson(result, seconds, commandLine, raw) with { Context = new AgentContext(model, skills, servers, null, dataCalls) { DownMcpServers = down }, ToolCalls = toolCalls };
    }

    /// <summary>The closing {"type":"result"} line of a stream-json output, or null.</summary>
    public static string? LastResultLine(string output)
    {
        foreach (var line in Lines(output).Reverse())
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (Text(doc.RootElement, "type") == "result")
                    return line;
            }
            catch (JsonException) { }
        }
        return null;
    }

    private static IEnumerable<string> Lines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// copilot -p --output-format json prints one event per line. The reply is the last assistant.message that
    /// requests no tools; skills and MCP servers come from the session events; the closing 'result' event has the exit
    /// code and premium requests. Copilot reports no token counts.
    /// </summary>
    public static AgentResult ReadCopilotJsonl(string output, double seconds, string commandLine, string raw)
    {
        string? reply = null, model = null;
        var skills = new List<string>();
        var servers = new List<string>();
        int turns = 0, toolCalls = 0;
        int? premium = null, exitCode = null;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                var type = Text(root, "type");
                root.TryGetProperty("data", out var data);
                switch (type)
                {
                    case "session.skills_loaded" when data.ValueKind == JsonValueKind.Object && data.TryGetProperty("skills", out var list):
                        foreach (var s in list.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.Object && s.TryGetProperty("enabled", out var on) && on.ValueKind != JsonValueKind.False))
                            if (Text(s, "name") is { } name && !skills.Contains(name))
                                skills.Add(name);
                        break;
                    case "session.mcp_servers_loaded" when data.ValueKind == JsonValueKind.Object && data.TryGetProperty("servers", out var list):
                        foreach (var s in list.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.Object))
                            if (Text(s, "name") is { } name && Text(s, "status") != "disabled" && !servers.Contains(name))
                                servers.Add(name);
                        break;
                    case "session.tools_updated" when data.ValueKind == JsonValueKind.Object:
                        model ??= Text(data, "model");
                        break;
                    case "assistant.turn_start":
                        turns++;
                        break;
                    case "assistant.message" when data.ValueKind == JsonValueKind.Object:
                        model = Text(data, "model") ?? model;
                        var requests = data.TryGetProperty("toolRequests", out var r) && r.ValueKind == JsonValueKind.Array ? r.GetArrayLength() : 0;
                        toolCalls += requests;
                        if (requests == 0 && !string.IsNullOrWhiteSpace(Text(data, "content")))
                            reply = Text(data, "content");
                        break;
                    case "result":
                        exitCode = root.TryGetProperty("exitCode", out var code) && code.ValueKind == JsonValueKind.Number ? code.GetInt32() : null;
                        if (root.TryGetProperty("usage", out var u))
                            premium = u.TryGetProperty("premiumRequests", out var p) && p.ValueKind == JsonValueKind.Number ? (int)p.GetDouble() : null;
                        break;
                }
            }
        }

        var context = new AgentContext(model, skills, servers, premium);
        if (exitCode is int c and not 0)
            return new AgentResult(false, null, $"Copilot finished with exit code {c}.", seconds, null, turns, commandLine, raw, context, toolCalls);
        return reply == null
            ? new AgentResult(false, null, "The output has no final assistant message.", seconds, null, turns, commandLine, raw, context, toolCalls)
            : new AgentResult(true, reply, null, seconds, null, turns, commandLine, raw, context, toolCalls);
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
}
