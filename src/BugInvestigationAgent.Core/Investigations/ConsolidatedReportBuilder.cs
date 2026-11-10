using System.Text;
using System.Text.Json;

namespace BugInvestigationAgent.Core.Investigations;

/// <summary>
/// Backlog-level report for one batch. Every ticket counts exactly once in the status and primary-assessment
/// totals; secondary observations are reported separately as flags.
/// </summary>
public static class ConsolidatedReportBuilder
{
    public sealed record Row(string IssueKey, InvestigationExecutionStatus Status, InvestigationRecord? Record, string? Error);

    public static IReadOnlyList<Row> Rows(LegacyBatch batch, IReadOnlyDictionary<string, InvestigationRecord> latest) =>
        batch.Items.OrderBy(i => i.Position)
            .Select(i => new Row(i.IssueKey, i.Status, latest.TryGetValue(i.IssueKey, out var r) ? r : null, i.LastError))
            .ToList();

    public static string ToMarkdown(LegacyBatch batch, IReadOnlyList<Row> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Legacy backlog assessment — {batch.Name}");
        sb.AppendLine();
        sb.AppendLine($"Filter {batch.FilterId} snapshot taken {batch.CreatedAt:yyyy-MM-dd HH:mm} UTC: **{rows.Count} tickets in scope**. Environment: {batch.EnvironmentName ?? batch.EnvironmentId}.");
        sb.AppendLine($"Membership check: {batch.LastMembershipChange ?? "not run yet"}");
        sb.AppendLine();
        sb.AppendLine("> AI-generated assessment for human review. \"Not reproduced\" is not proof that a bug is fixed; " +
                      "every \"potentially …\" category needs a person's decision. No Jira issue was modified and nothing was posted to Jira. " +
                      "The only change anywhere is a new local branch for each still-reproducible bug (not checked out, committed or pushed).");
        sb.AppendLine();

        sb.AppendLine("## Investigation status");
        foreach (var s in Enum.GetValues<InvestigationExecutionStatus>())
            sb.AppendLine($"- {s}: {rows.Count(r => r.Status == s)}");
        sb.AppendLine();

        var assessed = rows.Where(r => r.Record is not null).ToList();
        sb.AppendLine("## Primary assessment (each ticket counted once)");
        foreach (var a in Enum.GetValues<BugAssessment>())
            sb.AppendLine($"- {InvestigationAssessor.Label(a)}: {assessed.Count(r => r.Record!.Assessment == a)}");
        sb.AppendLine($"- Not yet investigated: {rows.Count - assessed.Count}");
        sb.AppendLine();

        var flagCounts = assessed.SelectMany(r => r.Record!.Flags).GroupBy(f => f).OrderByDescending(g => g.Count()).ToList();
        sb.AppendLine("## Additional observations (flags — a ticket can have several)");
        if (flagCounts.Count == 0)
            sb.AppendLine("- None.");
        foreach (var g in flagCounts)
            sb.AppendLine($"- {g.Key}: {g.Count()}");
        sb.AppendLine();

        sb.AppendLine("## Potential duplicate groups");
        var groups = DuplicateGroups(assessed.Select(r => r.Record!).ToList());
        if (groups.Count == 0)
            sb.AppendLine("- None found.");
        foreach (var g in groups)
            sb.AppendLine($"- {string.Join(", ", g)}");
        sb.AppendLine();

        Section(sb, "Potentially obsolete candidates (human review required)", assessed, r => Has(r, BugAssessment.PotentiallyObsolete));
        Section(sb, "Potentially critical issues requiring attention", assessed, r => Has(r, BugAssessment.CriticalCandidate));
        Section(sb, "Bugs with missing information", assessed, r => Has(r, BugAssessment.NeedsMoreInformation));
        Section(sb, "Possibly fixed (validate before closing)", assessed, r => Has(r, BugAssessment.PossiblyFixed));
        Section(sb, "Still exists (reproduced with evidence)", assessed, r => r.Assessment == BugAssessment.StillExists);
        Section(sb, "Confirmed fixed in the deployed version", assessed, r => r.Assessment == BugAssessment.ConfirmedFixed);

        sb.AppendLine("## Local branches created");
        var branches = assessed.Where(r => !string.IsNullOrWhiteSpace(r.Record!.CreatedBranch)).ToList();
        if (branches.Count == 0)
            sb.AppendLine("- None.");
        foreach (var r in branches)
            sb.AppendLine($"- {r.IssueKey}: {r.Record!.CreatedBranch} from {r.Record.SourceBranch} (KMS {r.Record.KmsVersion}) — fix: {InvestigationAssessor.Short(r.Record.ProposedFix ?? "see report", 160)}");
        sb.AppendLine();

        sb.AppendLine("## Inconclusive, blocked and failed investigations");
        var problems = rows.Where(r => r.Status is InvestigationExecutionStatus.Inconclusive or InvestigationExecutionStatus.Blocked or InvestigationExecutionStatus.Failed).ToList();
        if (problems.Count == 0)
            sb.AppendLine("- None.");
        foreach (var r in problems)
            sb.AppendLine($"- {r.IssueKey} — {r.Status}: {InvestigationAssessor.Short(r.Record?.ExecutionDetail ?? r.Error, 200)}");
        sb.AppendLine();

        sb.AppendLine("## Recommended next actions (all tickets)");
        sb.AppendLine("| Ticket | Status | Assessment | KMS version | Reproduction | Confidence | Local branch | Recommended action |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var r in rows)
        {
            var rec = r.Record;
            sb.AppendLine($"| {r.IssueKey} | {r.Status} | {(rec is null ? "—" : InvestigationAssessor.Label(rec.Assessment))} | {rec?.KmsVersion ?? "—"} | " +
                          $"{(rec is null ? "—" : InvestigationAssessor.Label(rec.ReproductionOutcome))} | {rec?.Confidence ?? "—"} | " +
                          $"{rec?.CreatedBranch ?? "—"} | {Cell(rec?.RecommendedAction ?? r.Error)} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Known limitations");
        sb.AppendLine("- Reproduction stops at ticket steps that need form data entry, waiting time or e-mail checks; those are listed per ticket.");
        sb.AppendLine("- Similar-issue and duplicate detection is keyword-based when no LLM is configured; duplicates are candidates, never confirmed.");
        sb.AppendLine("- Root causes come from code analysis of the configured workspace and are hypotheses unless a fix was verified by a retest.");
        sb.AppendLine("- \"Potentially obsolete\" is based on age and a failed reproduction only.");
        sb.AppendLine("- Sign-in is reported UNVERIFIED when the environment has no configured success selector.");
        return sb.ToString();
    }

    public static string ToJson(LegacyBatch batch, IReadOnlyList<Row> rows) => JsonSerializer.Serialize(new
    {
        batch = new { batch.Id, batch.Name, batch.FilterId, batch.Jql, batch.CreatedAt, batch.EnvironmentId, batch.LastMembershipChange },
        total = rows.Count,
        statuses = Enum.GetValues<InvestigationExecutionStatus>().ToDictionary(s => s.ToString(), s => rows.Count(r => r.Status == s)),
        assessments = Enum.GetValues<BugAssessment>().ToDictionary(a => a.ToString(), a => rows.Count(r => r.Record?.Assessment == a)),
        duplicateGroups = DuplicateGroups(rows.Where(r => r.Record is not null).Select(r => r.Record!).ToList()),
        tickets = rows.Select(r => new
        {
            r.IssueKey,
            status = r.Status.ToString(),
            assessment = r.Record?.Assessment.ToString(),
            flags = r.Record?.Flags,
            reproduction = r.Record?.ReproductionOutcome.ToString(),
            confidence = r.Record?.Confidence,
            rootCause = r.Record?.RootCause,
            rootCauseConfirmed = r.Record?.RootCauseConfirmed,
            similarIssues = r.Record?.SimilarIssueKeys,
            recommendedAction = r.Record?.RecommendedAction,
            kmsVersion = r.Record?.KmsVersion,
            sourceBranch = r.Record?.SourceBranch,
            createdBranch = r.Record?.CreatedBranch,
            proposedFix = r.Record?.ProposedFix,
            error = r.Error
        })
    }, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Connected groups of tickets assessed (or flagged) as potential duplicates and the issues they match.</summary>
    public static List<List<string>> DuplicateGroups(IReadOnlyList<InvestigationRecord> records)
    {
        var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Find(string k)
        {
            if (!parent.TryGetValue(k, out var p)) { parent[k] = k; return k; }
            return p.Equals(k, StringComparison.OrdinalIgnoreCase) ? k : parent[k] = Find(p);
        }
        void Union(string a, string b) => parent[Find(a)] = Find(b);

        foreach (var r in records.Where(r => Has(r, BugAssessment.PotentialDuplicate)))
        {
            Find(r.IssueKey);
            // Similar keys may include weaker candidates; the assessment reason names the strong ones.
            foreach (var k in r.SimilarIssueKeys.Where(k => (r.AssessmentReason ?? "").Contains(k, StringComparison.OrdinalIgnoreCase)))
                Union(r.IssueKey, k);
        }
        return parent.Keys.GroupBy(Find, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(k => k).ToList()).Where(g => g.Count > 1).OrderBy(g => g[0]).ToList();
    }

    private static bool Has(InvestigationRecord r, BugAssessment a) => r.Assessment == a || r.Flags.Contains(a.ToString());

    private static void Section(StringBuilder sb, string title, IEnumerable<Row> rows, Func<InvestigationRecord, bool> pick)
    {
        sb.AppendLine($"## {title}");
        var list = rows.Where(r => pick(r.Record!)).ToList();
        if (list.Count == 0)
            sb.AppendLine("- None.");
        foreach (var r in list)
            sb.AppendLine($"- {r.IssueKey} — {InvestigationAssessor.Short(r.Record!.AssessmentReason, 200)} (confidence {r.Record.Confidence})");
        sb.AppendLine();
    }

    private static string Cell(string? s) => InvestigationAssessor.Short(s, 160).Replace("|", "/").Replace("\n", " ");
}
