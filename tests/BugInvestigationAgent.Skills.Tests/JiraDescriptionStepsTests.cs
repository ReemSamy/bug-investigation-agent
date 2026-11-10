using System.Text.Json;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Skills.Jira;
using BugInvestigationAgent.Skills.Reproduction;

namespace BugInvestigationAgent.Skills.Tests;

/// <summary>VKMS-5102 shape: Jira Cloud returns the description as ADF with an orderedList of steps.</summary>
public class JiraDescriptionStepsTests
{
    private static string P(string text) => $$"""{"type":"paragraph","content":[{"type":"text","text":{{JsonSerializer.Serialize(text)}}}]}""";
    private static string Li(string text) => $$"""{"type":"listItem","content":[{{P(text)}}]}""";

    private static readonly string[] Steps =
    [
        "Create a new record.",
        "Set the MOC as temporary and ensure the responsible template is active. Set the expiration date to two days before the current date (-2 days).",
        "Verify that a BadgerMail notification is triggered.",
        "Change the expiration date to a future date.",
        "Wait 30 minutes, then change the expiration date back to two days before the current date (-2 days).",
        "Observe that no new BadgerMail notification is triggered."
    ];

    private static readonly string Adf = $$"""
        {"type":"doc","version":1,"content":[
          {{P("Scenario reproducing the issue:")}},
          {{P("A customer reported that after receiving a BadgerMail notification that a MOC was about to expire, they extended the expiration date.")}},
          {{P("Steps to reproduce:")}},
          {"type":"orderedList","attrs":{"order":1},"content":[{{string.Join(",", Steps.Select(Li))}}]},
          {{P("Actual result: BadgerMail does not trigger again after the expiration date is changed and then set back to the original expired date.")}}
        ]}
        """;

    [Test]
    public void Adf_numbered_list_keeps_one_line_per_step()
    {
        var text = AdfText.ToPlainText(JsonDocument.Parse(Adf).RootElement);

        Assert.That(text, Does.Contain("Steps to reproduce:\n1. Create a new record.\n2. Set the MOC as temporary"));
        Assert.That(JiraSkill.ExtractSteps(text), Is.EqualTo(Steps));
    }

    [Test]
    public void VKMS_5102_steps_start_in_MOC_and_stop_at_the_first_manual_step()
    {
        var description = AdfText.ToPlainText(JsonDocument.Parse(Adf).RootElement);
        var ctx = new RuntimeContext
        {
            JiraTicketKey = "VKMS-5102",
            Summary = "[12.28.6]Badgermail- Timed alert Badgermail checks the definition and sends a badgermail only once.",
            Description = description,
            ReproductionSteps = JiraSkill.ExtractSteps(description)
        };
        ctx.CustomFields["stepsFromJira"] = "true";

        var steps = TicketStepPlanner.ResolveSteps(ctx);
        TestContext.Out.WriteLine(string.Join("\n", steps));

        Assert.That(steps.Take(3), Is.EqualTo(new[]
        {
            "Open Modules menu",
            "Open module 'MOC'",
            "Click Create New (or Add) in Record Options to open the new record form"
        }));
        Assert.That(steps[3], Does.StartWith(TicketStepPlanner.ManualStepPrefix).And.Contain("data entry").And.Contain("temporary"));
        Assert.That(steps.Count(TicketStepPlanner.IsManualStep), Is.EqualTo(5), "steps 2-6 need a person (form data, e-mail, waiting)");
        Assert.That(steps, Has.None.Contains("12.28.6"));
    }

    [Test]
    public void Mock_is_not_mistaken_for_MOC()
    {
        var steps = TicketStepPlanner.PrepareTicketSteps(new[] { "Create a new record." }, "Reports: export broken", "Use the mock data set.");
        Assert.That(steps, Has.None.Contains("'MOC'"));
    }
}
