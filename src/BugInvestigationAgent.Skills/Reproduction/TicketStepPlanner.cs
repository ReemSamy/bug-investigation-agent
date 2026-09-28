using System.Text.RegularExpressions;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Skills.Jira;

namespace BugInvestigationAgent.Skills.Reproduction;

/// <summary>
/// Builds navigation/repro steps from THIS ticket's summary/description.
/// Prefer the full Steps to Reproduce text — never stop after the first admin screen.
/// Do not assume every area lives under the Modules menu.
/// </summary>
public static class TicketStepPlanner
{
    private static readonly string[] ModulesMenuAreas =
    [
        "Auditor", "Investigator", "Incident", "Tracker", "Resolution Tracker",
        "Risk Assessor", "Risk Intelligence", "Recurring Activities", "OII", "MOC", "Pre-MOC"
    ];

    private static readonly string[] AdministrationHints =
    [
        "Administration", "Admin", "Security Administration", "User Administration",
        "Checklist Administration", "Location Administration", "Workflow Administration",
        "Persons", "Person Administration", "Selection Administration", "Reports Administration"
    ];

    public static IReadOnlyList<string> ResolveSteps(RuntimeContext ctx)
    {
        if (ctx.CustomFields.ContainsKey("stepsFromJira") && ctx.ReproductionSteps.Count > 0)
        {
            var fromJira = ExpandAllSteps(ctx.ReproductionSteps);
            // Always keep going to the defect observation — tickets often end before Expected/Actual.
            EnsureObserveStep(fromJira, DescribeDefect(ctx.Summary, ctx.Description));
            ctx.ReproductionSteps = fromJira;
            return fromJira;
        }

        // Phase 2: only use historical steps when explicitly validated against THIS ticket.
        if (ctx.Investigation.CandidateStepsValidatedAgainstTicket &&
            ctx.Investigation.AdoptedHistoricalSteps.Count > 0)
        {
            var adopted = ExpandAllSteps(ctx.Investigation.AdoptedHistoricalSteps);
            EnsureObserveStep(adopted, DescribeDefect(ctx.Summary, ctx.Description));
            ctx.ReproductionSteps = adopted;
            ctx.CustomFields["stepsFromHistoricalValidated"] = "true";
            ctx.CustomFields["historicalStepsSource"] = ctx.Investigation.CandidateStepsSourceKey ?? "";
            return adopted;
        }

        var inferred = InferFromTicket(ctx);
        ctx.ReproductionSteps = inferred.ToList();
        ctx.CustomFields["stepsInferredFromTicket"] = "true";
        ctx.CustomFields["inferredForTicket"] = ctx.JiraTicketKey ?? "";
        return inferred;
    }

    public static IReadOnlyList<string> InferFromTicket(RuntimeContext ctx)
    {
        var summary = ctx.Summary ?? "";
        var description = ctx.Description ?? "";
        var corpus = $"{summary}\n{description}";
        var defect = DescribeDefect(summary, description);

        // Prefer the ticket's numbered Steps to Reproduce in full (all steps, not only the first path).
        var jiraSteps = JiraSkill.ExtractSteps(description);
        if (jiraSteps.Count > 0)
        {
            var expanded = ExpandAllSteps(jiraSteps);
            // Incomplete Jira paths (e.g. stop on Persons list) must still drill into the defect UI.
            if (LooksLikeMultiSelectAssigneeTypingDefect(corpus))
                EnsureMultiSelectAssigneeInteraction(expanded, defect, corpus);
            else
                EnsureObserveStep(expanded, defect);
            return Deduplicate(expanded);
        }

        var steps = new List<string>();
        var pathSteps = ExtractNavigationPathSteps(corpus);
        if (pathSteps.Count > 0)
            steps.AddRange(pathSteps);
        else
            steps.AddRange(BuildEntrySteps(ResolveEntryPoint(summary, corpus)));

        // Checklist / Text Response defects need the runtime Incident Checklist tab — not only admin setup.
        if (LooksLikeChecklistTextResponseDefect(corpus))
        {
            AppendChecklistTextResponseRuntimeSteps(steps, defect);
            return Deduplicate(steps);
        }

        // Multi-select assignee / stale filter typing — must open the control, type, select, reopen (not just land on Persons).
        if (LooksLikeMultiSelectAssigneeTypingDefect(corpus))
        {
            EnsureMultiSelectAssigneeInteraction(steps, defect, corpus);
            return Deduplicate(steps);
        }

        if (LooksLikeProposalFlow(corpus))
        {
            steps.Add("Open the first audit/proposal record from the list (grid link)");
            steps.Add("Confirm the Proposal form is open (not the list page)");
            if (Regex.IsMatch(corpus, @"(?i)\bsave\b"))
                steps.Add("Click Save (plain Save on the Proposal form)");
            steps.Add($"Observe whether this ticket's reported defect appears: {defect}");
            return Deduplicate(steps);
        }

        if (LooksLikeCreateNewFlow(summary, corpus))
        {
            steps.Add("Click Create New (or Add) in Record Options to open the new record form");
            steps.Add("Confirm the Add/Create form is open (not the list page)");
            if (LooksLikeValidationBug(corpus))
            {
                steps.Add("Leave required fields empty or invalid to trigger validation failure");
                steps.Add("Click Save or Submit on the form");
            }
            steps.Add($"Observe whether this ticket's reported defect appears: {defect}");
            return Deduplicate(steps);
        }

        var feature = ExtractFeature(summary, corpus);
        if (!string.IsNullOrWhiteSpace(feature) &&
            !steps.Any(s => s.Contains(feature, StringComparison.OrdinalIgnoreCase)))
            steps.Add($"Open '{feature}' from the current area UI");
        if (Regex.IsMatch(corpus, @"(?i)\bsave\b"))
            steps.Add("Click Save");
        steps.Add($"Observe whether this ticket's reported defect appears: {defect}");
        return Deduplicate(steps);
    }

    public static string DescribeDefect(string? summary, string? description = null)
    {
        var corpus = $"{summary}\n{description}";
        if (LooksLikeChecklistTextResponseDefect(corpus))
            return "missing Text Response input (empty field-wrapper / data-response-type=4 on Checklist tab)";
        if (LooksLikeMultiSelectAssigneeTypingDefect(corpus))
            return "multi-select assignee filter leaves previous typed text after select/reopen";
        if (Regex.IsMatch(corpus, @"(?i)cannot read properties|js error toast|raw js"))
            return "raw JS error toast / Cannot read properties…";
        if (Regex.IsMatch(corpus, @"(?i)sent for approval|confirmation message|misleading"))
        {
            var m = Regex.Match(corpus, "(?i)misleading\\s+['\"]([^'\"]+)['\"]");
            if (m.Success)
                return $"misleading confirmation message '{m.Groups[1].Value}'";
            return "misleading Save / approval confirmation message";
        }

        var summaryText = (summary ?? "").Trim();
        return string.IsNullOrWhiteSpace(summaryText) ? "the defect described in the ticket" : Truncate(summaryText, 140);
    }

    public static bool LooksLikeProposalFlow(string corpus) =>
        Regex.IsMatch(corpus, @"(?i)\bproposal\b") &&
        !Regex.IsMatch(corpus, @"(?i)\badd\s+audit\s+form\b");

    public static bool LooksLikeChecklistTextResponseDefect(string corpus) =>
        Regex.IsMatch(corpus, @"(?i)text\s*response") &&
        (Regex.IsMatch(corpus, @"(?i)field-wrapper|data-response-type|no input|renders\s+no|nothing clickable|cannot be answered") ||
         Regex.IsMatch(corpus, @"(?i)checklist"));

    /// <summary>
    /// Stale filter text in a multi-select assignee/people picker (Kendo MultiSelect input not cleared).
    /// Accepts common ticket typos (e.g. "Mutlti select").
    /// </summary>
    public static bool LooksLikeMultiSelectAssigneeTypingDefect(string corpus) =>
        Regex.IsMatch(corpus, @"(?i)mu[tl]{1,3}i[\s-]*select|multi[\s-]*select") &&
        (Regex.IsMatch(corpus, @"(?i)assignee") ||
         Regex.IsMatch(corpus, @"(?i)\b(team\s*members?|select\s+assignees?)\b")) &&
        Regex.IsMatch(corpus, @"(?i)typ(e|ing|ed)|previous|remain|leaves|stale|clear|filter");

    /// <summary>
    /// Expand every Jira/inferred step; arrow chains become multiple browser actions.
    /// </summary>
    public static List<string> ExpandAllSteps(IEnumerable<string> rawSteps)
    {
        var result = new List<string>();
        foreach (var raw in rawSteps)
        {
            foreach (var step in ExpandOneStep(raw))
            {
                if (!string.IsNullOrWhiteSpace(step))
                    result.Add(step);
            }
        }

        return Deduplicate(result);
    }

    public static IReadOnlyList<string> ExpandOneStep(string step)
    {
        var s = step.Trim();
        if (string.IsNullOrWhiteSpace(s))
            return Array.Empty<string>();

        // Arrow / chevron chains — keep slash inside tokens (create/select).
        if (Regex.IsMatch(s, @"\s*(?:→|>|»)\s*"))
            return ExpandArrowChain(s);

        return new[] { MapSegmentToStep(s, isFirst: true) };
    }

    public static IReadOnlyList<string> ExtractNavigationPathSteps(string corpus)
    {
        var steps = new List<string>();
        if (string.IsNullOrWhiteSpace(corpus))
            return steps;

        var breadcrumb = Regex.Match(
            corpus,
            @"(?im)\b((?:Modules|Administration|Admin|My Assignments)(?:\s*(?:→|>|»)\s*[^→>\n\r]{1,80}){1,8})");
        if (breadcrumb.Success)
            return ExpandArrowChain(breadcrumb.Groups[1].Value);

        var goTo = Regex.Match(
            corpus,
            @"(?im)\b(?:go to|navigate to|open|from)\s+((?:Modules|Administration|Admin)(?:\s*(?:→|>|»)\s*[^→>\n\r]{1,80}){0,8})");
        if (goTo.Success)
            return ExpandArrowChain(goTo.Groups[1].Value);

        if (Regex.IsMatch(corpus, @"(?i)\b(?:in|under|via|through)\s+Administration\b") ||
            Regex.IsMatch(corpus, @"(?i)\bAdministration\s*(?:→|>|»)"))
        {
            var area = ExtractAdminSubArea(corpus);
            steps.Add("Open Administration (app Administration area)");
            if (!string.IsNullOrWhiteSpace(area))
                steps.Add($"Open '{area}' inside Administration");
        }

        return steps;
    }

    public static NavigationEntry ResolveEntryPoint(string summary, string corpus)
    {
        if (LooksLikeAdministration(corpus) || LooksLikeAdministration(summary))
        {
            var sub = ExtractAdminSubArea(corpus) ?? ExtractAdminSubArea(summary);
            return new NavigationEntry(NavigationKind.Administration, sub);
        }

        var bracket = Regex.Match(summary, @"^\s*\[([^\]]+)\]");
        if (bracket.Success)
        {
            var label = CleanLabel(bracket.Groups[1].Value);
            if (IsAdministrationLabel(label))
                return new NavigationEntry(NavigationKind.Administration, null);
            if (IsModulesMenuArea(label))
                return new NavigationEntry(NavigationKind.Modules, label);
            return new NavigationEntry(NavigationKind.Direct, label);
        }

        var leadingPair = Regex.Match(summary, @"^\s*([A-Z][A-Za-z0-9]+)\s+([A-Z][A-Za-z0-9]+)\s*:");
        if (leadingPair.Success && !IsNoiseWord(leadingPair.Groups[1].Value))
        {
            var label = CleanLabel(leadingPair.Groups[1].Value);
            if (IsAdministrationLabel(label))
                return new NavigationEntry(NavigationKind.Administration, CleanLabel(leadingPair.Groups[2].Value));
            if (IsModulesMenuArea(label))
                return new NavigationEntry(NavigationKind.Modules, label);
            return new NavigationEntry(NavigationKind.Direct, label);
        }

        return new NavigationEntry(NavigationKind.Unknown, null);
    }

    public static IReadOnlyList<string> BuildEntrySteps(NavigationEntry entry)
    {
        var steps = new List<string>();
        switch (entry.Kind)
        {
            case NavigationKind.Modules:
                steps.Add("Open Modules menu");
                if (!string.IsNullOrWhiteSpace(entry.Target))
                    steps.Add($"Open module '{entry.Target}'");
                break;
            case NavigationKind.Administration:
                steps.Add("Open Administration (app Administration area)");
                if (!string.IsNullOrWhiteSpace(entry.Target) &&
                    !entry.Target.Equals("Administration", StringComparison.OrdinalIgnoreCase) &&
                    !entry.Target.Equals("Admin", StringComparison.OrdinalIgnoreCase))
                    steps.Add($"Open '{entry.Target}' inside Administration");
                break;
            case NavigationKind.Direct:
                if (!string.IsNullOrWhiteSpace(entry.Target))
                    steps.Add($"Open '{entry.Target}' from the application UI (as named in the ticket)");
                break;
        }

        return steps;
    }

    public enum NavigationKind
    {
        Unknown,
        Modules,
        Administration,
        Direct
    }

    public sealed record NavigationEntry(NavigationKind Kind, string? Target);

    private static List<string> ExpandArrowChain(string chain)
    {
        var parts = Regex.Split(chain, @"\s*(?:→|>|»)\s*")
            .Select(CleanLabel)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        var steps = new List<string>();
        for (var i = 0; i < parts.Count; i++)
            steps.Add(MapSegmentToStep(parts[i], isFirst: i == 0));
        return steps;
    }

    private static string MapSegmentToStep(string segment, bool isFirst)
    {
        var s = CleanLabel(segment);

        if (s.Equals("Modules", StringComparison.OrdinalIgnoreCase))
            return "Open Modules menu";
        if (s.Equals("Administration", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("Admin", StringComparison.OrdinalIgnoreCase))
            return "Open Administration (app Administration area)";

        if (Regex.IsMatch(s, @"(?i)^checklist$"))
            return "Open 'Investigator Checklists' / Checklist (Incident) inside Administration — not Pre-MOC";
        if (Regex.IsMatch(s, @"(?i)^incident$") && !Regex.IsMatch(s, @"(?i)create|open"))
            return "Open Incident checklist type (Investigator Checklists / Incident)";

        if (Regex.IsMatch(s, @"(?i)create/?select.*draft|select a draft|create.*draft"))
            return "Create New Draft or Select an existing Draft checklist";
        if (Regex.IsMatch(s, @"(?i)^add question$"))
            return "Click Add Question on the draft checklist";
        if (Regex.IsMatch(s, @"(?i)response type\s*=\s*text response|set response type.*text"))
            return "Set Response Type = Text Response";
        if (Regex.IsMatch(s, @"(?i)^save$"))
            return "Click Save";
        if (Regex.IsMatch(s, @"(?i)promote"))
            return "Promote the checklist to Active";
        if (Regex.IsMatch(s, @"(?i)create/?open an? incident|open an? incident|matching that checklist"))
            return "Create or open an Incident matching the checklist Location + Incident Type";
        if (Regex.IsMatch(s, @"(?i)open the checklist tab|checklist tab"))
            return "Open the Incident Checklist tab where answers are entered";

        // Multi-action remnants like "Save → Promote the checklist to Active" already split;
        // leftover compound phrases:
        if (Regex.IsMatch(s, @"(?i)set response type"))
            return "Set Response Type = Text Response";

        if (isFirst && IsModulesMenuArea(s))
            return $"Open module '{s}'";

        return $"Open '{s}' (next navigation step from the ticket path)";
    }

    private static void AppendChecklistTextResponseRuntimeSteps(List<string> steps, string defect)
    {
        if (!steps.Any(s => s.Contains("Investigator Checklists", StringComparison.OrdinalIgnoreCase) ||
                            s.Contains("Checklist", StringComparison.OrdinalIgnoreCase)))
        {
            steps.Add("Open Administration (app Administration area)");
            steps.Add("Open 'Investigator Checklists' / Checklist (Incident) inside Administration — not Pre-MOC");
            steps.Add("Open Incident checklist type (Investigator Checklists / Incident)");
        }

        if (!steps.Any(s => s.Contains("Draft", StringComparison.OrdinalIgnoreCase)))
            steps.Add("Create New Draft or Select an existing Draft checklist");
        if (!steps.Any(s => s.Contains("Add Question", StringComparison.OrdinalIgnoreCase)))
            steps.Add("Click Add Question on the draft checklist");
        if (!steps.Any(s => s.Contains("Text Response", StringComparison.OrdinalIgnoreCase)))
            steps.Add("Set Response Type = Text Response");
        steps.Add("Click Save");
        steps.Add("Promote the checklist to Active");
        steps.Add("Create or open an Incident matching the checklist Location + Incident Type");
        steps.Add("Open the Incident Checklist tab where answers are entered");
        steps.Add($"Observe whether this ticket's reported defect appears: {defect}");
    }

    private static void AppendMultiSelectAssigneeTypingSteps(List<string> steps, string defect)
    {
        EnsureMultiSelectAssigneeInteraction(steps, defect, corpus: "");
    }

    /// <summary>
    /// Extends whatever nav we already have so the agent enters the multi-select UI and exercises typing —
    /// never stop on Persons list with only an Observe step.
    /// </summary>
    public static void EnsureMultiSelectAssigneeInteraction(List<string> steps, string defect, string corpus)
    {
        var prefersPersons =
            steps.Any(s => s.Contains("Person", StringComparison.OrdinalIgnoreCase)) ||
            Regex.IsMatch(corpus, @"(?i)\bpersons?\b|\badministration\b");

        // Drop a trailing Observe so we can insert real interaction first.
        while (steps.Count > 0 && Regex.IsMatch(steps[^1], @"(?i)^observe"))
            steps.RemoveAt(steps.Count - 1);

        // Remove vague "add to an assignment" placeholders — replace with concrete Persons drill-in.
        steps.RemoveAll(s =>
            Regex.IsMatch(s, @"(?i)add to an assignment|from the current area UI") &&
            !Regex.IsMatch(s, @"(?i)person-plus|multi-select|type a partial|select a matching|reopen"));

        if (prefersPersons || steps.Any(s => s.Contains("Administration", StringComparison.OrdinalIgnoreCase)))
        {
            if (!steps.Any(s => s.Contains("Administration", StringComparison.OrdinalIgnoreCase)))
                steps.Insert(0, "Open Administration (app Administration area)");
            if (!steps.Any(s => s.Contains("Person", StringComparison.OrdinalIgnoreCase)))
                steps.Add("Open 'Persons' inside Administration");
            if (!steps.Any(s => Regex.IsMatch(s, @"(?i)person-plus|edit groups|change assignments|first person row")))
                steps.Add("Click person-plus / Groups (or Change Assignments) on the first active Persons row");
            if (!steps.Any(s => Regex.IsMatch(s, @"(?i)open the .*multi-select|Assignees multi-select|Groups multi-select")))
                steps.Add("Open the multi-select control on the dialog (Assignees / Groups / people picker)");
        }
        else
        {
            if (!steps.Any(s => s.Contains("Modules", StringComparison.OrdinalIgnoreCase)))
                steps.Insert(0, "Open Modules menu");
            if (!steps.Any(s => s.Contains("Recurring Activities", StringComparison.OrdinalIgnoreCase)))
                steps.Add("Open module 'Recurring Activities'");
            if (!steps.Any(s => Regex.IsMatch(s, @"(?i)Assignees multi-select|Select Assignees")))
                steps.Add("Open the Assignees multi-select filter (Select Assignees)");
        }

        if (!steps.Any(s => Regex.IsMatch(s, @"(?i)type a partial name")))
            steps.Add("Type a partial name into the multi-select filter input");
        if (!steps.Any(s => Regex.IsMatch(s, @"(?i)select a matching")))
            steps.Add("Select a matching assignee option from the multi-select list");
        if (!steps.Any(s => Regex.IsMatch(s, @"(?i)reopen")))
            steps.Add("Close or blur the multi-select, then reopen the Assignees multi-select");

        EnsureObserveStep(steps, defect);
    }

    private static void EnsureObserveStep(List<string> steps, string defect)
    {
        if (!steps.Any(s => Regex.IsMatch(s, @"(?i)^observe")))
            steps.Add($"Observe whether this ticket's reported defect appears: {defect}");
    }

    private static bool LooksLikeCreateNewFlow(string summary, string corpus) =>
        Regex.IsMatch(summary + "\n" + corpus, @"(?i)\b(add|create|new)\s+(audit|record|form|item)\b") ||
        Regex.IsMatch(summary, @"(?i)\badd\s+[a-z]+\s+form\b") ||
        Regex.IsMatch(summary, @"(?i)\[.+\]\s*add\b");

    private static bool LooksLikeAdministration(string text) =>
        Regex.IsMatch(text, @"(?i)\badministration\b") ||
        Regex.IsMatch(text, @"(?i)\badmin\s*(area|module|section|menu)?\b") ||
        AdministrationHints.Any(h => text.Contains(h, StringComparison.OrdinalIgnoreCase));

    private static bool IsAdministrationLabel(string label) =>
        label.Equals("Administration", StringComparison.OrdinalIgnoreCase) ||
        label.Equals("Admin", StringComparison.OrdinalIgnoreCase);

    private static bool IsModulesMenuArea(string label) =>
        ModulesMenuAreas.Any(a => label.Equals(a, StringComparison.OrdinalIgnoreCase) ||
                                  label.Contains(a, StringComparison.OrdinalIgnoreCase));

    private static string? ExtractAdminSubArea(string corpus)
    {
        var m = Regex.Match(
            corpus,
            @"(?i)\bAdministration\s*(?:→|>|»)\s*([A-Za-z][A-Za-z0-9 /_-]{1,48})");
        if (m.Success)
            return CleanLabel(m.Groups[1].Value);

        m = Regex.Match(
            corpus,
            @"(?i)\b((?:Security|Checklist|Location|Workflow|Persons?|Selection|Reports?|Equipment|Alerting|Methodology|Audit Template|Investigator Checklists)(?:\s+Administration)?)\b");
        if (m.Success && !m.Groups[1].Value.Equals("Administration", StringComparison.OrdinalIgnoreCase))
            return CleanLabel(m.Groups[1].Value);

        return null;
    }

    private static string? ExtractFeature(string summary, string corpus)
    {
        var addCreate = Regex.Match(
            summary + "\n" + corpus,
            @"(?i)\b((?:Add|Create|New|Edit)\s+[A-Za-z][A-Za-z0-9 /_-]{1,48}?)(?:\s+form|\s+page|\s+dialog|\s+leaks|\s+throws|\s+shows|,|\.|$)");
        if (addCreate.Success)
            return CleanLabel(addCreate.Groups[1].Value);

        var pair = Regex.Match(summary, @"^\s*(?:\[[^\]]+\]\s*)?([A-Z][A-Za-z0-9]+)\s+([A-Z][A-Za-z0-9][A-Za-z0-9 /_-]{0,40}?)\s*:");
        if (pair.Success)
            return CleanLabel(pair.Groups[2].Value);

        return null;
    }

    private static bool LooksLikeValidationBug(string corpus) =>
        Regex.IsMatch(corpus, @"(?i)\bvalidation\b") ||
        Regex.IsMatch(corpus, @"(?i)\btoast\b") ||
        Regex.IsMatch(corpus, @"(?i)cannot read properties") ||
        Regex.IsMatch(corpus, @"(?i)\bundefined\b");

    private static List<string> Deduplicate(List<string> steps)
    {
        var result = new List<string>();
        foreach (var step in steps)
        {
            if (result.Count > 0 &&
                string.Equals(result[^1], step, StringComparison.OrdinalIgnoreCase))
                continue;
            result.Add(step);
        }

        return result;
    }

    private static string CleanLabel(string value) =>
        Regex.Replace(value, @"\s+", " ").Trim().Trim('"', '\'', '“', '”', '.', ';');

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    private static bool IsNoiseWord(string value) =>
        value.Equals("The", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Bug", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Error", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Issue", StringComparison.OrdinalIgnoreCase);
}
