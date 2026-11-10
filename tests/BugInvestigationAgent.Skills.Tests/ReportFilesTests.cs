using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Core.Skills;
using BugInvestigationAgent.Core.Workflow;
using BugInvestigationAgent.Skills.Evidence;
using Microsoft.Extensions.Options;

namespace BugInvestigationAgent.Skills.Tests;

public class ReportFilesTests
{
    private string _root = "";

    [SetUp]
    public void SetUp() => _root = Path.Combine(Path.GetTempPath(), "bia-report-tests-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Test]
    public async Task Evidence_reporting_saves_a_redacted_report_that_can_be_listed_and_downloaded()
    {
        var ctx = new RuntimeContext
        {
            JiraTicketKey = "VKMS-5102",
            Summary = "Badgermail does not re-trigger",
            Description = "password=Hunter2!",
            ReproductionOutcome = ReproductionOutcome.NotReproduced,
            ReproductionNotes = "Login used password=Hunter2! then nothing happened",
            RootCause = "Alert is not re-queued."
        };
        var skill = new EvidenceReportingSkill(new SecretRedactor(), Options.Create(new AgentOptions { EvidenceDirectory = _root }));

        await skill.ExecuteAsync(new SkillExecutionRequest { Context = ctx, Permissions = null! });

        var folder = Path.Combine(_root, ctx.InvestigationId.ToString("N"));
        Assert.That(File.Exists(Path.Combine(folder, ReportFiles.JsonFileName)), Is.True);
        Assert.That(File.Exists(Path.Combine(folder, ReportFiles.MarkdownFileName)), Is.True);
        Assert.That(ctx.CustomFields.ContainsKey("reportSaved"), Is.True);
        foreach (var file in Directory.GetFiles(folder))
            Assert.That(File.ReadAllText(file), Does.Not.Contain("Hunter2!"), $"secret leaked into {Path.GetFileName(file)}");

        var listed = ReportFiles.List(_root);
        Assert.That(listed.Single().Report.IssueKey, Is.EqualTo("VKMS-5102"));
        Assert.That(listed.Single().Report.ReproductionOutcome, Is.EqualTo("NotReproduced"));
        Assert.That(ReportFiles.DownloadName(Path.Combine(folder, ReportFiles.MarkdownFileName)), Is.EqualTo("VKMS-5102_report.md"));
    }

    [Test]
    public void A_corrupt_report_does_not_break_the_list()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, Guid.NewGuid().ToString("N")));
        File.WriteAllText(Path.Combine(dir.FullName, ReportFiles.JsonFileName), "{ not json");
        Assert.That(ReportFiles.List(_root), Is.Empty);
    }
}
