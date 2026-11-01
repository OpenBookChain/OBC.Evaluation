using OBC.Evaluation.Core;
using OBC.Evaluation.Runner;

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
    public void A_prompt_is_the_skill_then_the_request()
    {
        var prompt = PromptBuilder.Build(Qa, "obc", "# Skill\n\nRules.\n");
        Assert.Equal("# Skill\n\nRules.\n\n---\n\n## Request\n\nQuestion: How much?\n", prompt);
    }

    [Fact]
    public void A_csv_prompt_names_the_data_file()
    {
        Assert.EndsWith("\nData file: base.csv\n", PromptBuilder.Build(Qa, "csv", "# Skill"));
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
        var result = AgentProcess.ReadClaudeJson(output, 12.5, "claude -p", output);
        Assert.True(result.Ok);
        Assert.Equal("{\"answer_status\":\"answered\"}", result.Reply);
        Assert.Equal(4, result.NumTurns);
        Assert.Equal(new AgentUsage(1200, 300, 5000, 800, 0.0123m), result.Usage);

        var failed = AgentProcess.ReadClaudeJson("""{"is_error":true,"result":"MCP server failed"}""", 1, "claude -p", "");
        Assert.False(failed.Ok);
        Assert.Contains("MCP server failed", failed.Error);
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
