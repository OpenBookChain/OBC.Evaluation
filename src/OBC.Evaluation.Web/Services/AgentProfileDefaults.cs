using System.Text.Json;
using System.Text.Json.Nodes;
using OBC.Evaluation.Core;

namespace OBC.Evaluation.Web.Services;

public static class AgentProfileDefaults
{
    private const string ObcUrl = "https://ai.openbookchain.com/mcp";
    private const string PlainUrl = "https://ai.openbookchain.com/mcp-postgres";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Create(string cli)
    {
        var codex = string.Equals(cli, "codex", StringComparison.OrdinalIgnoreCase);
        var setups = codex ? CodexSetups() : ClaudeSetups();
        return JsonSerializer.Serialize(new AgentProfileConfig { Cli = codex ? "codex" : "claude", Setups = setups }, Json);
    }

    /// <summary>
    /// Upgrades legacy Codex MCP profiles, which ignored the user's MCP config and therefore dropped
    /// authenticated connector configuration and did not approve calls to the selected MCP server.
    /// </summary>
    public static string UpgradeLegacyCodexMcpProfiles(string profileJson)
    {
        JsonObject? root;
        try { root = JsonNode.Parse(profileJson) as JsonObject; }
        catch (JsonException) { return profileJson; }
        if (root == null || !string.Equals(root["cli"]?.GetValue<string>(), "codex", StringComparison.OrdinalIgnoreCase) ||
            root["setups"] is not JsonObject setups)
            return profileJson;

        var changed = false;
        foreach (var (setupName, serverName) in new[] { ("obc", "obc_finai"), ("plain", "plain_postgres") })
        {
            if (setups[setupName] is not JsonObject setup || setup["args"] is not JsonArray args)
                continue;

            for (var i = args.Count - 1; i >= 0; i--)
                if (string.Equals(args[i]?.GetValue<string>(), "--ignore-user-config", StringComparison.Ordinal))
                {
                    args.RemoveAt(i);
                    changed = true;
                }

            var approvalSetting = $"mcp_servers.{serverName}.default_tools_approval_mode=\"approve\"";
            if (args.Any(arg => string.Equals(arg?.GetValue<string>(), approvalSetting, StringComparison.Ordinal)))
                continue;

            var urlIndex = -1;
            for (var i = 0; i + 1 < args.Count; i++)
                if (string.Equals(args[i]?.GetValue<string>(), "-c", StringComparison.Ordinal) &&
                    args[i + 1]?.GetValue<string>()?.StartsWith($"mcp_servers.{serverName}.url=", StringComparison.Ordinal) == true)
                {
                    urlIndex = i + 1;
                    break;
                }
            args.Insert(urlIndex < 0 ? args.Count : urlIndex + 1, JsonValue.Create("-c"));
            args.Insert(urlIndex < 0 ? args.Count : urlIndex + 2, JsonValue.Create(approvalSetting));
            changed = true;
        }
        return changed ? root.ToJsonString(Json) : profileJson;
    }

    private static Dictionary<string, AgentCommand> CodexSetups()
    {
        AgentCommand Codex(string? serverName, string? url)
        {
            var args = new List<string> { "exec", "--json", "--ephemeral" };
            if (serverName == null)
                args.Add("--ignore-user-config");
            args.AddRange(["--skip-git-repo-check", "--sandbox", "read-only", "--model", "{model}"]);
            if (serverName != null)
                args.AddRange(["-c", $"mcp_servers.{serverName}.url=\"{url}\""]);
            if (serverName != null)
                args.AddRange(["-c", $"mcp_servers.{serverName}.default_tools_approval_mode=\"approve\""]);
            args.Add("-");
            return new AgentCommand { Command = "codex", Args = args, Prompt = "stdin", Output = "codex-jsonl" };
        }

        return new Dictionary<string, AgentCommand>
        {
            ["obc"] = Codex("obc_finai", ObcUrl),
            ["plain"] = Codex("plain_postgres", PlainUrl),
            ["csv"] = Codex(null, null)
        };
    }

    private static Dictionary<string, AgentCommand> ClaudeSetups()
    {
        AgentCommand Claude(string? serverName, string? url, string[] allowedTools) => new()
        {
            Command = "claude",
            Args = [
                "-p", "--output-format", "json", "--model", "{model}",
                "--mcp-config", "{mcpConfigFile}", "--strict-mcp-config",
                "--allowedTools", .. allowedTools
            ],
            Prompt = "stdin",
            Output = "claude-json",
            McpConfigJson = serverName == null
                ? "{\"mcpServers\":{}}"
                : JsonSerializer.Serialize(new { mcpServers = new Dictionary<string, object>
                    { [serverName] = new { type = "http", url } } })
        };

        return new Dictionary<string, AgentCommand>
        {
            ["obc"] = Claude("obc", ObcUrl, ["mcp__obc"]),
            ["plain"] = Claude("plain", PlainUrl, ["mcp__plain"]),
            ["csv"] = Claude(null, null, ["Read", "Bash(python:*)"])
        };
    }
}
