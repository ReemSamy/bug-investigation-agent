using BugInvestigationAgent.Core.Authentication;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Credentials;
using BugInvestigationAgent.Core.Security;
using Moq;

namespace BugInvestigationAgent.Core.Tests;

public class MissingInfoResolverTests
{
    private static Mock<ICredentialVault> EmptyVault()
    {
        var vault = new Mock<ICredentialVault>();
        vault.Setup(v => v.FindForEnvironmentAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CredentialProfile?)null);
        return vault;
    }

    private static (MissingInfoResolver Resolver, InMemorySessionCredentialStore Store) Create()
    {
        var store = new InMemorySessionCredentialStore();
        return (new MissingInfoResolver(new FixedWorkspace(), new SignedInJira(), TestEnvironments.Selection(), store), store);
    }

    private static RuntimeContext LoadedTicket() => new()
    {
        JiraDetailsLoaded = true,
        WorkspacePath = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
        Username = "admin_dev",
        ReproductionSteps = { "Open page", "Click save" }
    };

    [Test]
    public async Task Asks_for_environment_and_credentials_before_reproduction()
    {
        var (resolver, _) = Create();
        var ctx = LoadedTicket();

        await resolver.ResolveAsync(ctx, EmptyVault().Object);

        Assert.That(ctx.MissingFields.Select(f => f.Key),
            Is.EquivalentTo(new[] { MissingInfoResolver.EnvironmentFieldKey, MissingInfoResolver.CredentialsFieldKey }));
        Assert.That(ctx.MissingFields.Single(f => f.Key == "environment").InputType, Is.EqualTo("environment"));
    }

    [Test]
    public async Task Missing_credentials_are_requested_for_the_selected_environment()
    {
        var (resolver, _) = Create();
        var ctx = LoadedTicket();
        TestEnvironments.Selection().Select(ctx, "KMSRC");
        ctx.UserConfirmedReadyToReproduce = true;

        await resolver.ResolveAsync(ctx, EmptyVault().Object);

        var creds = ctx.MissingFields.Single(f => f.Key == "credentials");
        Assert.That(creds.Label, Does.Contain("KMSRC"));
        Assert.That(creds.Reason, Does.Contain("admin_dev"));
        Assert.That(ctx.MissingFields.Any(f => f.Key == "environment"), Is.False, "confirmed, valid environment is not asked again");
    }

    [Test]
    public async Task Ready_once_environment_confirmed_and_credentials_entered_for_it()
    {
        var (resolver, store) = Create();
        var ctx = LoadedTicket();
        TestEnvironments.Selection().Select(ctx, "KMSRC");
        store.Set(ctx.InvestigationId, "KMSRC", new CredentialSecret { Username = "admin_dev", Password = "pw" });
        ctx.UserConfirmedReadyToReproduce = true;

        await resolver.ResolveAsync(ctx, EmptyVault().Object);

        Assert.That(ctx.MissingFields, Is.Empty);
    }

    [Test]
    public async Task Credentials_for_another_environment_are_not_accepted()
    {
        var (resolver, store) = Create();
        var ctx = LoadedTicket();
        TestEnvironments.Selection().Select(ctx, "KMSQA");
        store.Set(ctx.InvestigationId, "KMSRC", new CredentialSecret { Username = "admin_dev", Password = "pw" });
        ctx.UserConfirmedReadyToReproduce = true;

        await resolver.ResolveAsync(ctx, EmptyVault().Object);

        Assert.That(ctx.MissingFields.Any(f => f.Key == "credentials"), Is.True);
    }

    [Test]
    public async Task Saved_profile_of_another_environment_is_discarded()
    {
        var (resolver, _) = Create();
        var rcProfile = new CredentialProfile { Id = Guid.NewGuid(), EnvironmentId = "KMSRC", OwnerId = "me", Username = "admin_dev" };
        var vault = EmptyVault();
        vault.Setup(v => v.GetProfileAsync(rcProfile.Id, It.IsAny<CancellationToken>())).ReturnsAsync(rcProfile);

        var ctx = LoadedTicket();
        TestEnvironments.Selection().Select(ctx, "KMSQA");
        ctx.CredentialProfileId = rcProfile.Id;
        ctx.UserConfirmedReadyToReproduce = true;

        await resolver.ResolveAsync(ctx, vault.Object);

        Assert.That(ctx.CredentialProfileId, Is.Null);
        Assert.That(ctx.MissingFields.Any(f => f.Key == "credentials"), Is.True);
    }

    [Test]
    public async Task Saved_profile_for_the_selected_environment_is_offered_and_accepted()
    {
        var (resolver, _) = Create();
        var qaProfile = new CredentialProfile { Id = Guid.NewGuid(), EnvironmentId = "KMSQA", OwnerId = "me", Username = "qa_admin" };
        var vault = new Mock<ICredentialVault>();
        vault.Setup(v => v.FindForEnvironmentAsync("KMSQA", It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(qaProfile);
        vault.Setup(v => v.GetProfileAsync(qaProfile.Id, It.IsAny<CancellationToken>())).ReturnsAsync(qaProfile);

        var ctx = LoadedTicket();
        TestEnvironments.Selection().Select(ctx, "KMSQA");
        await resolver.ResolveAsync(ctx, vault.Object);
        Assert.That(ctx.MissingFields.Single(f => f.Key == "credentials").Reason, Does.Contain("Saved credentials found for 'qa_admin'"));

        ctx.CredentialProfileId = qaProfile.Id;
        ctx.UserConfirmedReadyToReproduce = true;
        await resolver.ResolveAsync(ctx, vault.Object);
        Assert.That(ctx.MissingFields, Is.Empty);
    }

    [Test]
    public async Task Missing_environment_url_configuration_is_explained()
    {
        var (resolver, _) = Create();
        var ctx = LoadedTicket();
        TestEnvironments.Selection().Select(ctx, "KMSDEV");

        await resolver.ResolveAsync(ctx, EmptyVault().Object);

        Assert.That(ctx.MissingFields.Single(f => f.Key == "environment").Reason, Does.Contain("no Base URL"));
        Assert.That(ctx.MissingInfoReason, Does.Contain("no Base URL"));
    }

    [Test]
    public async Task Mfa_pending_only_asks_the_user_to_complete_the_step()
    {
        var (resolver, _) = Create();
        var ctx = LoadedTicket();
        TestEnvironments.Selection().Select(ctx, "KMSRC");
        ctx.AuthenticationStatus = AuthenticationStatus.MfaRequired;
        ctx.AuthenticationMessage = "Enter the verification code.";

        await resolver.ResolveAsync(ctx, EmptyVault().Object);
        Assert.That(ctx.MissingFields.Single().InputType, Is.EqualTo("auth-manual"));

        ctx.AuthenticationRecheckRequested = true;
        await resolver.ResolveAsync(ctx, EmptyVault().Object);
        Assert.That(ctx.MissingFields, Is.Empty);
    }

    [Test]
    public async Task Rejects_azure_devops_pr_as_environment_and_asks_for_environment()
    {
        var (resolver, _) = Create();
        var ctx = LoadedTicket();
        ctx.EnvironmentUrl = "https://dev.azure.com/EHSQ/VisiumKMS/_git/kms-suite/pullrequest/3081";
        ctx.EnvironmentName = "dev.azure.com";
        ctx.FixPullRequestUrl = "https://dev.azure.com/EHSQ/VisiumKMS/_git/kms-suite/pullrequest/3081";

        await resolver.ResolveAsync(ctx, EmptyVault().Object);

        Assert.That(ctx.EnvironmentUrl, Is.Null);
        Assert.That(ctx.EnvironmentName, Is.Null);
        Assert.That(ctx.MissingFields.Single(f => f.Key == "environment").Reason, Does.Contain("fix PR"));
    }
}

public class EnvironmentUrlParserTests
{
    [Test]
    public void Rejects_ado_pr_and_accepts_azurewebsites_portal()
    {
        Assert.That(
            EnvironmentUrlParser.FindFirstUrl(
                "See https://dev.azure.com/EHSQ/VisiumKMS/_git/kms-suite/pullrequest/3081"),
            Is.Null);

        Assert.That(
            EnvironmentUrlParser.FindFirstUrl(
                "Environment: https://kms14-portal-win-rel-scus.azurewebsites.net"),
            Is.EqualTo("https://kms14-portal-win-rel-scus.azurewebsites.net"));

        Assert.That(
            EnvironmentUrlParser.IsLikelyPortalEnvironmentUrl(
                "https://dev.azure.com/org/project/_git/repo"),
            Is.False);
    }

    [Test]
    public void Finds_fix_pr_url_separately_from_portal()
    {
        const string text =
            "Fixed in https://dev.azure.com/EHSQ/VisiumKMS/_git/kms-suite/pullrequest/3081 — test on https://kms14-portal-win-rel-scus.azurewebsites.net";

        Assert.That(
            EnvironmentUrlParser.FindFixPullRequestUrl(text),
            Is.EqualTo("https://dev.azure.com/EHSQ/VisiumKMS/_git/kms-suite/pullrequest/3081"));
        Assert.That(
            EnvironmentUrlParser.FindFirstUrl(text),
            Is.EqualTo("https://kms14-portal-win-rel-scus.azurewebsites.net"));
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
