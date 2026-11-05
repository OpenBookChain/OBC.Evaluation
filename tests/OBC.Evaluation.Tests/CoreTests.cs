using OBC.Evaluation.Core;

namespace OBC.Evaluation.Tests;

public class CoreTests
{
    private static readonly EvalTask Qa = new() { Id = "B1.1@500", Kind = TaskKinds.Qa, Request = "Question: How much?", DataFile = "base.csv" };
    private static readonly EvalTask Audit = new() { Id = "C1-audit@500", Kind = TaskKinds.Audit, Request = "Audit the report and working papers below.", DataFile = "base.csv" };

    private const string PreparerReply = """
        <<<REPORT>>>
        Total income was £10.00 [C1].
        <<<END REPORT>>>
        <<<WORKING PAPERS>>>
        WP-C1: receipt 0123456789abcdef0123456789abcdef
        <<<END WORKING PAPERS>>>
        <<<CLAIMS JSON>>>
        {"claims":[{"tag":"C1","label":"Total income","value":10.0,"unit":"GBP","working_paper":"WP-C1"}]}
        <<<END CLAIMS JSON>>>
        """;

    [Fact]
    public void A_prompt_is_the_skill_then_the_request_then_the_usage_ask()
    {
        var prompt = PromptBuilder.Build(Qa, "obc", "# Skill\n\nRules.\n");
        Assert.StartsWith("# Skill\n\nRules.\n\n---\n\n## Request\n\nQuestion: How much?\n\n", prompt);
        Assert.EndsWith(UsageReport.Instruction(Qa.Kind), prompt);
    }

    [Fact]
    public void A_qa_reply_can_report_its_usage_in_the_json()
    {
        var usage = UsageReport.TryParse(TaskKinds.Qa, "{\"final_answer\": \"1\", \"usage_report\": {\"input_tokens\": 1200, \"output_tokens\": 300, \"seconds\": 14.5}}");
        Assert.Equal(new UsageReport(1200, 300, 14.5), usage);
        Assert.Null(UsageReport.TryParse(TaskKinds.Qa, "{\"final_answer\": \"1\"}"));
        Assert.Null(UsageReport.TryParse(TaskKinds.Qa, "{\"usage_report\": {\"input_tokens\": null, \"output_tokens\": null, \"seconds\": null}}"));
    }

    [Fact]
    public void A_block_reply_reports_its_usage_in_its_own_block_without_a_warning()
    {
        var reply = PreparerReply + "\n<<<USAGE>>>\n{\"input_tokens\": 900, \"output_tokens\": null, \"seconds\": 20}\n<<<END USAGE>>>";
        Assert.Equal(new UsageReport(900, null, 20), UsageReport.TryParse(TaskKinds.Prepare, reply));
        Assert.Equal(ReplyCheck.Valid, ReplyValidator.Check(TaskKinds.Prepare, "plain", reply).Status);
    }

    [Fact]
    public void A_csv_prompt_names_the_data_file()
    {
        Assert.Contains("\nData file: ./base.csv\n", PromptBuilder.Build(Qa, "csv", "# Skill"));
        Assert.DoesNotContain("Data file", PromptBuilder.Build(Qa, "plain", "# Skill"));
    }

    [Fact]
    public void An_auditor_gets_the_report_and_working_papers_but_not_the_claims_json()
    {
        var source = PromptBuilder.AuditSource(PreparerReply);
        var prompt = PromptBuilder.Build(Audit, "obc", "# Auditor", source);
        Assert.Contains("<<<REPORT>>>\nTotal income was £10.00 [C1].\n<<<END REPORT>>>", prompt);
        Assert.Contains("<<<WORKING PAPERS>>>", prompt);
        Assert.DoesNotContain("CLAIMS JSON", prompt);
        Assert.Throws<InvalidOperationException>(() => PromptBuilder.Build(Audit, "obc", "# Auditor"));
    }

    [Fact]
    public void A_preparer_reply_with_all_blocks_is_valid_and_its_receipts_are_found()
    {
        var check = ReplyValidator.Check(TaskKinds.Prepare, "obc", PreparerReply);
        Assert.Equal(ReplyCheck.Valid, check.Status);
        Assert.Equal(["0123456789abcdef0123456789abcdef"], check.ReceiptIds);
    }

    [Fact]
    public void A_missing_block_is_invalid_and_text_outside_the_blocks_is_an_issue()
    {
        var missing = ReplyValidator.Check(TaskKinds.Prepare, "plain", PreparerReply.Replace("<<<END CLAIMS JSON>>>", ""));
        Assert.Equal(ReplyCheck.Invalid, missing.Status);
        Assert.Contains(missing.Messages, m => m.Contains("CLAIMS JSON"));

        var chatty = ReplyValidator.Check(TaskKinds.Prepare, "plain", "Here is the report:\n" + PreparerReply);
        Assert.Equal(ReplyCheck.Issues, chatty.Status);
    }

    [Fact]
    public void Auditor_verdicts_must_be_from_the_list()
    {
        const string reply = """
            <<<AUDIT MEMO>>>
            Qualified.
            <<<END AUDIT MEMO>>>
            <<<AUDITOR WORKING PAPERS>>>
            AWP-C1
            <<<END AUDITOR WORKING PAPERS>>>
            <<<VERDICTS JSON>>>
            {"opinion":"qualified","verdicts":[{"tag":"C1","verdict":"@V@"}]}
            <<<END VERDICTS JSON>>>
            """;
        Assert.Equal(ReplyCheck.Valid, ReplyValidator.Check(TaskKinds.Audit, "csv", reply.Replace("@V@", "evidence_changed")).Status);
        Assert.Equal(ReplyCheck.Invalid, ReplyValidator.Check(TaskKinds.Audit, "csv", reply.Replace("@V@", "looks fine")).Status);
    }

    [Fact]
    public void A_qa_reply_must_be_the_answer_json()
    {
        const string json = """{"answer_status":"answered","answer_type":"number","final_answer":1.5,"unit":"GBP","method":"Summed.","assumptions":[],"data_warnings":[],"source_references":[],"confidence":"high"}""";
        Assert.Equal(ReplyCheck.Valid, ReplyValidator.Check(TaskKinds.Qa, "obc", json).Status);
        Assert.Equal(ReplyCheck.Issues, ReplyValidator.Check(TaskKinds.Qa, "obc", "```json\n" + json + "\n```").Status);
        Assert.Equal(ReplyCheck.Invalid, ReplyValidator.Check(TaskKinds.Qa, "obc", json.Replace("\"answered\"", "\"maybe\"")).Status);
        Assert.Equal(ReplyCheck.Invalid, ReplyValidator.Check(TaskKinds.Qa, "obc", "I think it is 1.5").Status);
    }

    [Fact]
    public void The_runner_reads_claude_json_output()
    {
        const string output = """{"type":"result","is_error":false,"result":"{\"answer_status\":\"answered\"}","num_turns":4,"total_cost_usd":0.0123,"usage":{"input_tokens":1200,"output_tokens":300,"cache_read_input_tokens":5000,"cache_creation_input_tokens":800}}""";
        var result = AgentOutput.ReadClaudeJson(output, 12.5, "claude -p", output);
        Assert.True(result.Ok);
        Assert.Equal("{\"answer_status\":\"answered\"}", result.Reply);
        Assert.Equal(4, result.NumTurns);
        Assert.Equal(new AgentUsage(1200, 300, 5000, 800, 0.0123m), result.Usage);

        var failed = AgentOutput.ReadClaudeJson("""{"is_error":true,"result":"MCP server failed"}""", 1, "claude -p", "");
        Assert.False(failed.Ok);
        Assert.Contains("MCP server failed", failed.Error);
    }

    [Fact]
    public void The_runner_counts_the_connector_tool_calls_of_a_claude_stream()
    {
        var output = string.Join(Environment.NewLine,
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"ToolSearch","input":{}}]}}""",
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"mcp__claude_ai_plain_postgres__execute_raw_query","input":{}}]}}""",
            """{"type":"result","is_error":false,"result":"done","num_turns":3}""");

        var result = AgentOutput.ReadClaudeStream(output, 5, "claude -p", output);

        Assert.Equal(2, result.ToolCalls);
        Assert.Equal(1, result.Context!.DataToolCalls);
    }

    [Fact]
    public void The_runner_sees_a_claude_mcp_server_that_needs_sign_in()
    {
        var output = string.Join("\n",
            """{"type":"system","subtype":"init","model":"m","skills":[],"mcp_servers":[{"name":"plain","status":"needs-auth"},{"name":"obc","status":"connected"},{"name":"late","status":"pending"}]}""",
            """{"type":"result","is_error":false,"result":"done","num_turns":1}""");

        var result = AgentOutput.ReadClaudeStream(output, 1, "claude -p", output);

        Assert.Equal(["plain (needs-auth)"], result.Context!.DownMcpServers);
    }

    [Fact]
    public void The_runner_reads_claude_stream_events_the_reply_and_what_the_run_had()
    {
        var output = string.Join("\n",
            """{"type":"system","subtype":"init","model":"claude-opus-5-5","skills":[],"mcp_servers":[{"name":"claude.ai OBC SyncDB MCP","status":"pending"}]}""",
            """{"type":"assistant","message":{"content":[]}}""",
            """{"type":"result","is_error":false,"result":"OK","num_turns":2,"total_cost_usd":0.05,"usage":{"input_tokens":10,"output_tokens":3}}""");

        var result = AgentOutput.ReadClaudeStream(output, 5, "claude -p", output);

        Assert.True(result.Ok);
        Assert.Equal("OK", result.Reply);
        Assert.Equal(2, result.NumTurns);
        Assert.Equal(0.05m, result.Usage!.CostUsd);
        Assert.Equal("claude-opus-5-5", result.Context!.Model);
        Assert.Empty(result.Context.Skills);
        Assert.Equal(["claude.ai OBC SyncDB MCP"], result.Context.McpServers);

        var failed = AgentOutput.ReadClaudeStream("""{"type":"result","is_error":true,"result":"Not logged in"}""", 1, "claude -p", "");
        Assert.False(failed.Ok);
        Assert.Contains("Not logged in", failed.Error);
        Assert.False(AgentOutput.ReadClaudeStream("""{"type":"system","subtype":"init"}""", 1, "claude -p", "").Ok);
    }

    [Fact]
    public void A_failed_exit_carries_the_tools_own_reason()
    {
        var claude = new AgentCommand { Command = "C:/x/claude.exe", Output = "claude-json" };
        Assert.Equal("claude.exe exited with code 1. Not logged in. Please run /login",
            AgentProcess.ExitMessage(claude, 1, """{"is_error":true,"result":"Not logged in. Please run /login"}""", "", 1, "claude"));
        Assert.Equal("claude.exe exited with code 1. Error: Invalid MCP configuration:",
            AgentProcess.ExitMessage(claude, 1, "", "Error: Invalid MCP configuration:\nMCP config file not found", 0, "claude"));
        Assert.Equal("copilot exited with code 2. unknown option",
            AgentProcess.ExitMessage(new AgentCommand { Command = "copilot", Output = "copilot-jsonl" }, 2, "", "unknown option\nmore", 1, "copilot"));
    }

    [Fact]
    public void The_runner_reads_copilot_events_the_reply_the_model_the_skills_and_the_premium_requests()
    {
        var output = string.Join("\n",
            """{"type":"session.mcp_servers_loaded","data":{"servers":[{"name":"obc","status":"connected"},{"name":"github-mcp-server","status":"disabled"}]}}""",
            """{"type":"session.skills_loaded","data":{"skills":[{"name":"caveman","enabled":true},{"name":"off","enabled":false}]}}""",
            """{"type":"session.tools_updated","data":{"model":"gpt-5.3-codex"}}""",
            """{"type":"assistant.turn_start","data":{"turnId":"0"}}""",
            """{"type":"assistant.message","data":{"content":"","model":"gpt-5.3-codex","toolRequests":[{"name":"query"},{"name":"query"}]}}""",
            """{"type":"assistant.turn_start","data":{"turnId":"1"}}""",
            """{"type":"assistant.message","data":{"content":"{\"answer_status\":\"answered\"}","model":"gpt-5.3-codex","toolRequests":[]}}""",
            """{"type":"result","exitCode":0,"usage":{"premiumRequests":6,"totalApiDurationMs":1474}}""");

        var result = AgentOutput.ReadCopilotJsonl(output, 9, "copilot -p", output);

        Assert.True(result.Ok);
        Assert.Equal("{\"answer_status\":\"answered\"}", result.Reply);
        Assert.Equal((2, 2), (result.NumTurns, result.ToolCalls));
        Assert.Equal(new AgentContext("gpt-5.3-codex", ["caveman"], ["obc"], 6), result.Context, new ContextComparer());

        var failed = AgentOutput.ReadCopilotJsonl("""{"type":"result","exitCode":1,"usage":{}}""", 1, "copilot -p", "");
        Assert.False(failed.Ok);
    }

    private sealed class ContextComparer : IEqualityComparer<AgentContext?>
    {
        public bool Equals(AgentContext? a, AgentContext? b) =>
            a != null && b != null && a.Model == b.Model && a.PremiumRequests == b.PremiumRequests
            && a.Skills.SequenceEqual(b.Skills) && a.McpServers.SequenceEqual(b.McpServers);

        public int GetHashCode(AgentContext? c) => c?.Model?.GetHashCode() ?? 0;
    }

    [Fact]
    public void The_repo_bundle_loads_and_its_matrix_is_the_planned_145_runs()
    {
        var bundle = Bundle.Load(Path.Combine(TestPaths.RepoRoot, "bundle"));
        Assert.Equal(145, bundle.Tasks.Where(t => !t.HeldOut).Sum(t => t.Setups.Count));
        Assert.All(bundle.Tasks.Where(t => t.DependsOn != null), t => Assert.Equal(TaskKinds.Prepare, bundle.Task(t.DependsOn!).Kind));
        Assert.All(bundle.Tasks, t => Assert.True(bundle.SkillTexts.ContainsKey(t.SkillFor("obc", bundle.QaSkill))));
    }

    [Fact]
    public void A_changed_skill_file_stops_the_bundle_from_loading()
    {
        var copy = TestPaths.CopyBundle();
        File.AppendAllText(Path.Combine(copy, "skills", "prepare-obc.md"), "\nEdited.");
        var ex = Assert.Throws<InvalidDataException>(() => Bundle.Load(copy));
        Assert.Contains("prepare-obc.md", ex.Message);
    }
}

internal static class TestPaths
{
    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "OBC.Evaluation.slnx")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new DirectoryNotFoundException("Repo root not found.");
        }
    }

    public static string CopyBundle()
    {
        var target = Path.Combine(Path.GetTempPath(), "obc-eval-tests", Guid.NewGuid().ToString("N"), "bundle");
        foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot, "bundle"), "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(Path.Combine(RepoRoot, "bundle"), file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
        return target;
    }
}
