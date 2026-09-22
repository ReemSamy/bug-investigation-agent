using BugInvestigationAgent.Skills.Environment;
using BugInvestigationAgent.Skills.Jira;
using BugInvestigationAgent.Skills.Validation;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Permissions;
using BugInvestigationAgent.Core.Skills;

namespace BugInvestigationAgent.Skills.Tests;

public class EnvironmentDiscoverySkillTests
{
    [Test]
    public async Task Extracts_environment_user_and_url_from_ticket_text()
    {
        var ctx = new RuntimeContext
        {
            Summary = "Login fails on portal",
            Description = """
                Bug reproduced on Portal 14 or rc using admin_dev.
                Environment URL: https://rc.example.test/app
                Role: coordinator
                Steps to reproduce:
                1. Open assignments
                2. Click Save
                """,
            JiraDetailsLoaded = true
        };

        var skill = new EnvironmentDiscoverySkill();
        var result = await skill.ExecuteAsync(new SkillExecutionRequest
        {
            Context = ctx,
            Permissions = new PermissionGate(AgentPermission.ReadJira)
        });

        Assert.That(result.Succeeded, Is.True);
        Assert.That(ctx.EnvironmentUrl, Is.EqualTo("https://rc.example.test/app"));
        Assert.That(ctx.Username, Is.EqualTo("admin_dev").Or.EqualTo("coordinator"));
        Assert.That(ctx.EnvironmentName, Is.Not.Null.And.Not.Empty);
        Assert.That(ctx.SystemName, Is.Null.Or.Not.EqualTo("Login"));
    }

    [Test]
    public async Task Parses_bare_azurewebsites_host_and_does_not_use_issue_key_as_system()
    {
        var ctx = new RuntimeContext
        {
            JiraTicketKey = "VKMS-4180",
            SystemName = "VKMS-4180",
            Summary = "On the Auditor Add Audit form…",
            Description = """
                Environment: kms14-portal-win-rel-scus.azurewebsites.net using Chrome browser.
                Steps to Reproduce:
                1. Navigate to Auditor -> Create New
                2. Click Save
                """,
            JiraDetailsLoaded = true
        };

        var skill = new EnvironmentDiscoverySkill();
        await skill.ExecuteAsync(new SkillExecutionRequest
        {
            Context = ctx,
            Permissions = new PermissionGate(AgentPermission.ReadJira)
        });

        Assert.That(ctx.EnvironmentUrl, Is.EqualTo("https://kms14-portal-win-rel-scus.azurewebsites.net"));
        Assert.That(ctx.SystemName, Is.Null);
    }
}

public class JiraSkillParsingTests
{
    [Test]
    public void ExtractSteps_reads_numbered_reproduction_block()
    {
        var steps = JiraSkill.ExtractSteps("""
            Something else
            Steps to reproduce:
            1. Open page
            2. Click button
            Expected:
            It works
            """);

        Assert.That(steps, Is.EqualTo(new[] { "Open page", "Click button" }));
    }
}

public class ValidationAllowlistTests
{
    [Test]
    public void Accepts_exact_and_prefixed_allowlisted_commands_only()
    {
        var allow = new[] { "dotnet build", "dotnet test" };
        Assert.That(ValidationSkill.IsAllowlisted("dotnet build", allow), Is.True);
        Assert.That(ValidationSkill.IsAllowlisted("dotnet test --filter Foo", allow), Is.True);
        Assert.That(ValidationSkill.IsAllowlisted("rm -rf /", allow), Is.False);
        Assert.That(ValidationSkill.IsAllowlisted("dotnet", allow), Is.False);
    }
}

public class MissingInfoResolverTests
{
    [Test]
    public void Rejects_urls_as_workspace_paths()
    {
        Assert.That(MissingInfoResolver.IsLocalWorkspace("https://dev.azure.com/org/proj/_git/repo"), Is.False);
        Assert.That(MissingInfoResolver.IsLocalWorkspace("https://kms14-portal-win-rel-scus.azurewebsites.net/"), Is.False);
        Assert.That(MissingInfoResolver.IsLocalWorkspace(Path.GetTempPath().TrimEnd('\\')), Is.True);
    }
}
