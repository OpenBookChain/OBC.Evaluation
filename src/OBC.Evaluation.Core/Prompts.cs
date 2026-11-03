namespace OBC.Evaluation.Core;

/// <summary>
/// Builds the one text a run is given. The manual wizard shows it to paste into a chat app, and the agent API
/// sends it to a headless agent, so both flows give a model exactly the same words.
/// </summary>
public static class PromptBuilder
{
    public static string Build(EvalTask task, string setup, string skillText, string? auditSource = null)
    {
        var text = skillText.TrimEnd() + "\n\n---\n\n## Request\n\n" + task.Request.Trim() + "\n";
        if (TaskKinds.IsAudit(task.Kind))
        {
            if (string.IsNullOrWhiteSpace(auditSource))
                throw new InvalidOperationException($"{task.Id} needs the preparer's report and working papers.");
            text += "\n" + auditSource.Trim() + "\n";
        }
        text += "\n" + UsageReport.Instruction(task.Kind);
        if (setup == "csv")
            text += $"\nData file: ./{task.DataFile}\nThe file is in your current working directory. Read it from disk before answering; do not assume its contents or report that it is unavailable without checking.\n";
        return text;
    }

    /// <summary>What an auditor is given from a preparer's reply: its report and working papers, nothing else.</summary>
    public static string AuditSource(string preparerReply)
    {
        var blocks = Blocks.Parse(preparerReply);
        if (!blocks.TryGetValue(Blocks.Report, out var report) || !blocks.TryGetValue(Blocks.WorkingPapers, out var papers))
            throw new InvalidOperationException("The preparer's reply has no REPORT or WORKING PAPERS block.");
        return Blocks.Wrap(Blocks.Report, report) + "\n" + Blocks.Wrap(Blocks.WorkingPapers, papers);
    }
}
