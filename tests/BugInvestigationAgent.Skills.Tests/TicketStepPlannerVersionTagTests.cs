using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Skills.Reproduction;

namespace BugInvestigationAgent.Skills.Tests;

public class TicketStepPlannerVersionTagTests
{
    [TestCase("[12.28.6]Badgermail- Timed alert", "Badgermail- Timed alert")]
    [TestCase("[v14.2] [Release 12.28] Incident: crash", "Incident: crash")]
    [TestCase("[Incident] Save fails", "[Incident] Save fails")]
    [TestCase("No tag here", "No tag here")]
    public void StripVersionTags_removes_only_version_brackets(string summary, string expected) =>
        Assert.That(TicketStepPlanner.StripVersionTags(summary), Is.EqualTo(expected));

    [Test]
    public void VKMS_5102_navigates_to_Badgermail_not_the_version_number()
    {
        // Real ticket shape: version tag in brackets, feature name before the dash, no Jira steps.
        var ctx = new RuntimeContext
        {
            JiraTicketKey = "VKMS-5102",
            Summary = "[12.28.6]Badgermail- Timed alert Badgermail checks the definition and sends a badgermail only once.",
            Description = ""
        };

        var steps = TicketStepPlanner.ResolveSteps(ctx);

        TestContext.Out.WriteLine(string.Join("\n", steps));
        Assert.That(steps, Has.None.Contains("12.28.6"), "no step may target the version number");
        Assert.That(steps[0], Does.Contain("'Badgermail'"));
    }

    [Test]
    public void Bracketed_feature_name_is_still_used_as_the_entry_point()
    {
        var entry = TicketStepPlanner.ResolveEntryPoint("[Tracker] Export button missing", "");
        Assert.That(entry.Kind, Is.EqualTo(TicketStepPlanner.NavigationKind.Modules));
        Assert.That(entry.Target, Is.EqualTo("Tracker"));
    }
}
