using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Credentials;
using BugInvestigationAgent.Core.Security;
using Moq;

namespace BugInvestigationAgent.Core.Tests;

public class MissingInfoResolverTests
{
    [Test]
    public async Task Asks_for_credentials_when_other_fields_present()
    {
        var vault = new Mock<ICredentialVault>();
        vault.Setup(v => v.FindMatchAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CredentialProfile?)null);

        var ctx = new RuntimeContext
        {
            JiraDetailsLoaded = true,
            WorkspacePath = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
            EnvironmentUrl = "https://example.test/app",
            Username = "admin_dev",
            SystemName = "SystemX",
            EnvironmentName = "rc",
            ReproductionSteps = { "Open page", "Click save" }
        };

        await new MissingInfoResolver().ResolveAsync(ctx, vault.Object);

        Assert.That(ctx.MissingFields.Count, Is.EqualTo(1));
        Assert.That(ctx.MissingFields[0].Key, Is.EqualTo("credentials"));
        Assert.That(ctx.MissingFields[0].Reason, Does.Contain("admin_dev"));
    }

    [Test]
    public async Task Still_asks_confirm_when_credential_profile_matches()
    {
        var profile = new CredentialProfile { Id = Guid.NewGuid(), Label = "SystemX + admin_dev", Username = "admin_dev" };
        var vault = new Mock<ICredentialVault>();
        vault.Setup(v => v.FindMatchAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);

        var ctx = new RuntimeContext
        {
            JiraDetailsLoaded = true,
            WorkspacePath = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
            EnvironmentUrl = "https://example.test/app",
            Username = "admin_dev",
            ReproductionSteps = { "Open page" }
        };

        await new MissingInfoResolver().ResolveAsync(ctx, vault.Object);

        Assert.That(ctx.CredentialProfileId, Is.EqualTo(profile.Id));
        Assert.That(ctx.MissingFields.Any(f => f.Key == "credentials"), Is.True);
        Assert.That(ctx.MissingFields.Single(f => f.Key == "credentials").Reason, Does.Contain("Saved credentials"));
    }

    [Test]
    public async Task Ready_after_user_confirms_with_matched_profile()
    {
        var profile = new CredentialProfile { Id = Guid.NewGuid(), Label = "SystemX + admin_dev", Username = "admin_dev" };
        var vault = new Mock<ICredentialVault>();
        vault.Setup(v => v.FindMatchAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);

        var ctx = new RuntimeContext
        {
            JiraDetailsLoaded = true,
            WorkspacePath = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
            EnvironmentUrl = "https://example.test/app",
            Username = "admin_dev",
            CredentialProfileId = profile.Id,
            UserConfirmedReadyToReproduce = true,
            ReproductionSteps = { "Open page" }
        };

        await new MissingInfoResolver().ResolveAsync(ctx, vault.Object);

        Assert.That(ctx.MissingFields, Is.Empty);
    }
}

public class SecretRedactorTests
{
    [Test]
    public void Redacts_registered_secrets_and_password_patterns()
    {
        var redactor = new SecretRedactor();
        redactor.RegisterSecret("SuperSecret123!");
        var text = redactor.Redact("token=abc password=SuperSecret123! keep");
        Assert.That(text, Does.Not.Contain("SuperSecret123!"));
        Assert.That(text, Does.Contain("[REDACTED]"));
    }
}
