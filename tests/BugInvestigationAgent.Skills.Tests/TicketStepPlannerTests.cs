using System.Text.RegularExpressions;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Skills.Jira;
using BugInvestigationAgent.Skills.Reproduction;

namespace BugInvestigationAgent.Skills.Tests;

public class TicketStepPlannerTests
{
    private const string Vkms4167Description =
        """
        Found during: VKMS-REG-M2-017
        Environment: kms14-portal-win-rel-scus
        Severity: P1

        Steps to Reproduce

        Administration → Checklist → Incident → create/select a Draft checklist → Add Question

        Set Response Type = Text Response → Save → Promote the checklist to Active

        Create/open an Incident matching that checklist's Location + Incident Type → open the Checklist tab

        Expected
        The question's Response column shows a text input/textarea

        Actual
        The Response cell renders a completely empty <div class="field-wrapper" data-response-type="4"></div>
        """;

    [Test]
    public void Infers_auditor_add_audit_path_from_summary()
    {
        var ctx = new RuntimeContext
        {
            Summary = "[Auditor] Add Audit form leaks a raw JS error toast (\"Cannot read properties of undefined (reading 'Index')\") on validation failure",
            Description = "Environment: kms14..."
        };

        var steps = TicketStepPlanner.InferFromTicket(ctx);

        Assert.That(steps.Any(s => s.Contains("Modules", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(steps.Any(s => s.Contains("Auditor", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(steps.Any(s => s.Contains("Create New", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(steps.Any(s => s.Contains("grid", StringComparison.OrdinalIgnoreCase)), Is.False);
    }

    [Test]
    public void Proposal_ticket_opens_grid_record_not_create_new()
    {
        var ctx = new RuntimeContext
        {
            JiraTicketKey = "VKMS-4183",
            Summary = "Auditor Proposal: plain Save shows misleading 'sent for approval' confirmation message",
            Description = "Windows RC"
        };

        var steps = TicketStepPlanner.InferFromTicket(ctx);
        var defect = TicketStepPlanner.DescribeDefect(ctx.Summary, ctx.Description);

        Assert.That(steps.Any(s => s.Contains("Auditor", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(steps.Any(s => s.Contains("Modules", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(steps.Any(s => s.Contains("grid", StringComparison.OrdinalIgnoreCase) || s.Contains("first audit", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(steps.Any(s => s.Contains("Create New", StringComparison.OrdinalIgnoreCase)), Is.False);
        Assert.That(steps.Any(s => s.Contains("Save", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(defect, Does.Contain("confirmation").IgnoreCase.Or.Contain("approval").IgnoreCase);
    }

    [Test]
    public void Administration_ticket_does_not_force_modules_menu()
    {
        var ctx = new RuntimeContext
        {
            Summary = "Persons grid export fails after filter",
            Description =
                """
                Steps to reproduce:
                1. Go to Administration > Persons
                2. Apply a location filter
                3. Click Export to Excel
                Expected: file downloads
                Actual: error toast
                """
        };

        var steps = TicketStepPlanner.InferFromTicket(ctx);

        Assert.That(steps.Any(s => s.Contains("Administration", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(steps.Any(s => s.Contains("Persons", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(steps.Any(s => s.Equals("Open Modules menu", StringComparison.OrdinalIgnoreCase)), Is.False);
    }

    [Test]
    public void ResolveSteps_keeps_and_expands_jira_administration_path()
    {
        var ctx = new RuntimeContext
        {
            Summary = "Checklist admin bug",
            Description = "see steps",
            ReproductionSteps =
            {
                "Navigate to Administration > Checklist Administration",
                "Select a checklist and click Promote"
            }
        };
        ctx.CustomFields["stepsFromJira"] = "true";

        var steps = TicketStepPlanner.ResolveSteps(ctx);

        Assert.That(steps.Any(s => s.Contains("Administration", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(steps.Any(s => s.Contains("Checklist", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(steps.Any(s => s.Contains("Promote", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(steps.Any(s => s.Equals("Open Modules menu", StringComparison.OrdinalIgnoreCase)), Is.False);
    }

    [Test]
    public void Vkms4167_extracts_all_three_repro_blocks()
    {
        var extracted = JiraSkill.ExtractSteps(Vkms4167Description);
        Assert.That(extracted.Count, Is.GreaterThanOrEqualTo(3));
        Assert.That(extracted.Any(s => s.Contains("Administration", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(extracted.Any(s => s.Contains("Text Response", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(extracted.Any(s => s.Contains("Checklist tab", StringComparison.OrdinalIgnoreCase)), Is.True);
    }

    [Test]
    public void Vkms4167_expands_full_path_past_add_question_to_incident_checklist_tab()
    {
        var ctx = new RuntimeContext
        {
            JiraTicketKey = "VKMS-4167",
            Summary = "[Module 2 - Checklists] \"Text Response\" question type renders NO input control on Investigator Checklist tab — cannot be answered at all",
            Description = Vkms4167Description
        };

        var steps = TicketStepPlanner.InferFromTicket(ctx);
        var defect = TicketStepPlanner.DescribeDefect(ctx.Summary, ctx.Description);

        Assert.That(steps.Any(s => s.Contains("Administration", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(steps.Any(s => s.Contains("Add Question", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(steps.Any(s => s.Contains("Text Response", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(steps.Any(s => s.Contains("Promote", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(steps.Any(s => s.Contains("Incident", StringComparison.OrdinalIgnoreCase) && s.Contains("Checklist", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(steps.Any(s => s.Contains("Pre-MOC", StringComparison.OrdinalIgnoreCase) && s.Contains("not", StringComparison.OrdinalIgnoreCase)), Is.True);
        // Must not stop at admin Add Question only.
        var stepList = steps.ToList();
        var addQuestionIndex = stepList.FindIndex(s => s.Contains("Add Question", StringComparison.OrdinalIgnoreCase));
        var checklistTabIndex = stepList.FindIndex(s => s.Contains("Checklist tab", StringComparison.OrdinalIgnoreCase));
        Assert.That(addQuestionIndex, Is.GreaterThanOrEqualTo(0));
        Assert.That(checklistTabIndex, Is.GreaterThan(addQuestionIndex));
        Assert.That(defect, Does.Contain("Text Response").IgnoreCase);
        Assert.That(defect, Does.Contain("field-wrapper").IgnoreCase);
    }

    [Test]
    public void Vkms4241_infers_multiselect_assignee_interaction_not_persons_list_only()
    {
        var ctx = new RuntimeContext
        {
            JiraTicketKey = "VKMS-4241",
            Summary = "V14 - Beta - Mutlti select assignee screen leaves previous typing",
            Description = "When using the multi select assignee screen, previous typed text remains."
        };

        var steps = TicketStepPlanner.InferFromTicket(ctx);
        var defect = TicketStepPlanner.DescribeDefect(ctx.Summary, ctx.Description);

        Assert.That(TicketStepPlanner.LooksLikeMultiSelectAssigneeTypingDefect($"{ctx.Summary}\n{ctx.Description}"), Is.True);
        Assert.That(steps.Any(s => s.Contains("Persons", StringComparison.OrdinalIgnoreCase) || s.Contains("Recurring Activities", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(steps.Any(s => s.Contains("person-plus", StringComparison.OrdinalIgnoreCase) || s.Contains("Assignees multi-select", StringComparison.OrdinalIgnoreCase) || s.Contains("multi-select control", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(steps.Any(s => s.Contains("Type a partial name", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(steps.Any(s => s.Contains("Select a matching assignee", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(steps.Any(s => s.Contains("reopen", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(steps.Any(s => s.Contains("add to an assignment", StringComparison.OrdinalIgnoreCase)), Is.False);
        Assert.That(defect, Does.Contain("multi-select").IgnoreCase);
    }

    [Test]
    public void Incomplete_jira_persons_path_gets_multiselect_interaction_appended()
    {
        var ctx = new RuntimeContext
        {
            JiraTicketKey = "VKMS-4241",
            Summary = "V14 - Beta - Mutlti select assignee screen leaves previous typing",
            Description =
                """
                Steps to Reproduce
                1. Go to Administration > Persons
                2. Open add to an assignment
                """
        };

        var steps = TicketStepPlanner.InferFromTicket(ctx);

        Assert.That(steps.Any(s => s.Contains("Type a partial name", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(steps.Any(s => s.Contains("reopen", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(steps.Count(s => Regex.IsMatch(s, @"(?i)^observe")), Is.EqualTo(1));
    }
}
