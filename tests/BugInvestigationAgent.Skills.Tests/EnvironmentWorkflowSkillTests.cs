using System.Text.Json;
using BugInvestigationAgent.Core.Authentication;
using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Credentials;
using BugInvestigationAgent.Core.Environments;
using BugInvestigationAgent.Core.Permissions;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Core.Skills;
using BugInvestigationAgent.Core.Workflow;
using BugInvestigationAgent.Skills.Browser;
using BugInvestigationAgent.Skills.Browser.Authentication;
using BugInvestigationAgent.Skills.Environment;
using BugInvestigationAgent.Skills.Evidence;
using BugInvestigationAgent.Skills.Reproduction;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using Moq;

namespace BugInvestigationAgent.Skills.Tests;

internal static class SkillTestEnvironments
{
    public static EnvironmentSelectionService Selection() => new(new ConfigurationEnvironmentRegistry(new EnvironmentRegistryOptions
    {
        Environments = new Dictionary<string, EnvironmentDefinitionOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["KMSRC"] = new() { ApplicationId = "KMS", BaseUrl = "https://kmsrc.example.test", Type = EnvironmentType.ReleaseCandidate },
            ["KMSQA"] = new() { ApplicationId = "KMS", BaseUrl = "https://kmsqa.example.test", Type = EnvironmentType.QA },
            ["KMSDEV"] = new() { ApplicationId = "KMS", BaseUrl = "", Type = EnvironmentType.Development }
        }
    }));

    public static SkillExecutionRequest Request(RuntimeContext ctx, IReadOnlyDictionary<string, string>? inputs = null) => new()
    {
        Context = ctx,
        Permissions = new PermissionGate((AgentPermission)int.MaxValue),
        Inputs = inputs
    };
}

public class EnvironmentRegistryConfigurationTests
{
    [Test]
    public void Binds_environments_keyed_by_id_and_supports_overrides_without_code()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EnvironmentRegistry:Environments:KMSRC:ApplicationId"] = "KMS",
                ["EnvironmentRegistry:Environments:KMSRC:BaseUrl"] = "",
                ["EnvironmentRegistry:Environments:KMSRC:Type"] = "ReleaseCandidate",
                ["EnvironmentRegistry:Environments:KMSQA:ApplicationId"] = "KMS",
                ["EnvironmentRegistry:Environments:KMSQA:Type"] = "QA",
                ["EnvironmentRegistry:Environments:KMSQA:Enabled"] = "false",
                ["EnvironmentRegistry:Environments:KMSQA:Aliases:0"] = "KMS QA"
            })
            // e.g. appsettings.Local.json or an environment variable overriding a single value
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EnvironmentRegistry:Environments:KMSRC:BaseUrl"] = "https://kmsrc.example.test"
            })
            .Build();

        var services = new ServiceCollection();
        services.Configure<EnvironmentRegistryOptions>(config.GetSection(EnvironmentRegistryOptions.SectionName));
        services.AddSingleton<IEnvironmentRegistry, ConfigurationEnvironmentRegistry>();
        var registry = services.BuildServiceProvider().GetRequiredService<IEnvironmentRegistry>();

        var rc = registry.Get("KMSRC")!;
        var qa = registry.Get("KMSQA")!;
        Assert.That(rc.BaseUrl, Is.EqualTo("https://kmsrc.example.test"));
        Assert.That(rc.Type, Is.EqualTo(EnvironmentType.ReleaseCandidate));
        Assert.That(qa.Enabled, Is.False);
        Assert.That(qa.Aliases, Does.Contain("KMS QA"));
    }

    [Test]
    public void Shipped_appsettings_registers_KMSRC_and_KMSQA_without_urls_or_credentials()
    {
        var path = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory,
            "..", "..", "..", "..", "..", "src", "BugInvestigationAgent.App", "appsettings.json"));
        var config = new ConfigurationBuilder().AddJsonFile(path).Build();
        var options = config.GetSection(EnvironmentRegistryOptions.SectionName).Get<EnvironmentRegistryOptions>()!;

        Assert.That(options.Environments.Keys, Does.Contain("KMSRC").And.Contain("KMSQA"));
        Assert.That(options.Environments.Values.All(e => string.IsNullOrEmpty(e.BaseUrl)), Is.True, "no invented URLs in source control");
        var raw = File.ReadAllText(path);
        Assert.That(raw, Does.Not.Contain("\"Password\"").IgnoreCase);
    }
}

public class EnvironmentDiscoverySuggestionTests
{
    [Test]
    public async Task Discovery_suggests_environment_but_does_not_confirm_it()
    {
        var ctx = new RuntimeContext { Summary = "Save fails", Description = "Happens on KMSRC only", JiraDetailsLoaded = true };

        await new EnvironmentDiscoverySkill(SkillTestEnvironments.Selection()).ExecuteAsync(SkillTestEnvironments.Request(ctx));

        Assert.That(ctx.SuggestedEnvironmentId, Is.EqualTo("KMSRC"));
        Assert.That(ctx.SelectedEnvironmentId, Is.Null);
        Assert.That(ctx.EnvironmentSelectionConfirmed, Is.False);
    }
}

public class BrowserAutomationPreconditionTests
{
    private static BrowserAutomationSkill Create(Mock<IPlaywrightSessionHub> hub, ISessionCredentialStore store, Mock<ICredentialVault>? vault = null) =>
        new(
            (vault ?? new Mock<ICredentialVault>()).Object,
            store,
            SkillTestEnvironments.Selection(),
            new SecretRedactor(),
            Options.Create(new AgentOptions { EvidenceDirectory = Path.GetTempPath() }),
            hub.Object,
            new LoginFlow(),
            NullLogger<BrowserAutomationSkill>.Instance);

    [Test]
    public async Task Does_not_open_a_browser_without_a_selected_environment()
    {
        var hub = new Mock<IPlaywrightSessionHub>(MockBehavior.Strict);
        var ctx = new RuntimeContext();

        await Create(hub, new InMemorySessionCredentialStore()).ExecuteAsync(SkillTestEnvironments.Request(ctx));

        Assert.That(ctx.ReproductionOutcome, Is.EqualTo(ReproductionOutcome.Blocked));
        Assert.That(ctx.AuthenticationStatus, Is.Not.EqualTo(AuthenticationStatus.Succeeded));
        hub.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Does_not_open_a_browser_for_an_environment_missing_its_url()
    {
        var hub = new Mock<IPlaywrightSessionHub>(MockBehavior.Strict);
        var ctx = new RuntimeContext { SelectedEnvironmentId = "KMSDEV", EnvironmentSelectionConfirmed = true };

        await Create(hub, new InMemorySessionCredentialStore()).ExecuteAsync(SkillTestEnvironments.Request(ctx));

        Assert.That(ctx.ReproductionNotes, Does.Contain("no Base URL"));
        hub.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Requests_credentials_instead_of_signing_in_without_them()
    {
        var hub = new Mock<IPlaywrightSessionHub>(MockBehavior.Strict);
        var store = new InMemorySessionCredentialStore();
        var ctx = new RuntimeContext();
        SkillTestEnvironments.Selection().Select(ctx, "KMSQA");
        // Credentials typed for ANOTHER environment must not be used.
        store.Set(ctx.InvestigationId, "KMSRC", new CredentialSecret { Username = "rc", Password = "pw" });

        await Create(hub, store).ExecuteAsync(SkillTestEnvironments.Request(ctx));

        Assert.That(ctx.AuthenticationStatus, Is.EqualTo(AuthenticationStatus.CredentialsRequired));
        Assert.That(ctx.AuthenticationMessage, Does.Contain("KMSQA"));
        hub.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Saved_profile_of_another_environment_is_never_resolved()
    {
        var hub = new Mock<IPlaywrightSessionHub>(MockBehavior.Strict);
        var rcProfile = new CredentialProfile { Id = Guid.NewGuid(), EnvironmentId = "KMSRC", OwnerId = "me", Username = "rc" };
        var vault = new Mock<ICredentialVault>();
        vault.Setup(v => v.GetProfileAsync(rcProfile.Id, It.IsAny<CancellationToken>())).ReturnsAsync(rcProfile);
        var ctx = new RuntimeContext();
        SkillTestEnvironments.Selection().Select(ctx, "KMSQA");
        ctx.CredentialProfileId = rcProfile.Id;

        await Create(hub, new InMemorySessionCredentialStore(), vault).ExecuteAsync(SkillTestEnvironments.Request(ctx));

        vault.Verify(v => v.ResolveSecretAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.That(ctx.AuthenticationStatus, Is.EqualTo(AuthenticationStatus.CredentialsRequired));
        Assert.That(ctx.CredentialProfileId, Is.Null);
    }

    [Test]
    public void Start_url_appends_configured_login_path()
    {
        var env = new AppEnvironment
        {
            Id = "X", DisplayName = "X", BaseUrl = "https://x.example.test/",
            Login = new LoginFormOptions { LoginPath = "/Account/Login" }.WithDefaults()
        };
        Assert.That(BrowserAutomationSkill.BuildStartUrl(env), Is.EqualTo("https://x.example.test/Account/Login"));
    }
}

public class ReproductionAuthenticationGateTests
{
    [TestCase(AuthenticationStatus.NotStarted)]
    [TestCase(AuthenticationStatus.CredentialsRequired)]
    [TestCase(AuthenticationStatus.InvalidCredentials)]
    [TestCase(AuthenticationStatus.MfaRequired)]
    [TestCase(AuthenticationStatus.Failed)]
    public async Task Reproduction_never_starts_before_successful_sign_in(AuthenticationStatus status)
    {
        var hub = new Mock<IPlaywrightSessionHub>(MockBehavior.Strict);
        var skill = new ReproductionSkill(Options.Create(new AgentOptions()), new SecretRedactor(), hub.Object);
        var ctx = new RuntimeContext { EnvironmentUrl = "https://kmsqa.example.test", AuthenticationStatus = status };

        await skill.ExecuteAsync(SkillTestEnvironments.Request(ctx));
        await skill.ExecuteAsync(SkillTestEnvironments.Request(ctx, new Dictionary<string, string> { ["mode"] = "retest" }));

        Assert.That(ctx.ReproductionOutcome, Is.EqualTo(ReproductionOutcome.Blocked));
        Assert.That(ctx.ReproductionNotes, Does.Contain("successful sign-in"));
        hub.VerifyNoOtherCalls();
    }
}

public class EvidenceAssociationTests
{
    [Test]
    public void Evidence_items_carry_ticket_investigation_and_environment()
    {
        var ctx = new RuntimeContext { JiraTicketKey = "VKMS-42" };
        SkillTestEnvironments.Selection().Select(ctx, "KMSQA");

        var item = EvidenceItem.For(ctx, "screenshot", "x.png", "after login", EvidenceCategories.PostLogin);

        Assert.That(item.JiraTicketKey, Is.EqualTo("VKMS-42"));
        Assert.That(item.InvestigationId, Is.EqualTo(ctx.InvestigationId));
        Assert.That(item.EnvironmentId, Is.EqualTo("KMSQA"));
        Assert.That(item.Category, Is.EqualTo(EvidenceCategories.PostLogin));
        Assert.That(EvidenceScreenshots.FileName(ctx, "02-after-login"), Is.EqualTo("VKMS-42_KMSQA_02-after-login.png"));
    }

    [Test]
    public void Password_fields_are_always_masked_in_screenshots()
    {
        var masks = EvidenceScreenshots.MaskSelectors(new LoginFormOptions { MaskSelectors = ["#email"] });
        Assert.That(masks, Does.Contain("input[type=password]").And.Contain("#email"));
    }

    [Test]
    public async Task Report_and_manifest_identify_environment_and_never_contain_credentials()
    {
        var evidenceRoot = Path.Combine(Path.GetTempPath(), "bia-evidence-" + Guid.NewGuid().ToString("N"));
        try
        {
            var redactor = new SecretRedactor();
            redactor.RegisterSecret("Sup3r-Secret-pw");
            var ctx = new RuntimeContext
            {
                JiraTicketKey = "VKMS-7",
                Username = "qa_admin",
                AuthenticationStatus = AuthenticationStatus.Succeeded,
                ReproductionOutcome = ReproductionOutcome.NotReproduced
            };
            SkillTestEnvironments.Selection().Select(ctx, "KMSQA");
            ctx.Evidence.Add(EvidenceItem.For(ctx, "screenshot", Path.Combine(evidenceRoot, "a.png"),
                "typed password=Sup3r-Secret-pw by mistake", EvidenceCategories.Workflow));

            var skill = new EvidenceReportingSkill(redactor, Options.Create(new AgentOptions { EvidenceDirectory = evidenceRoot }));
            await skill.ExecuteAsync(SkillTestEnvironments.Request(ctx));

            var summary = ctx.Evidence.Single(e => e.Kind == "summary").Caption!;
            Assert.That(summary, Does.Contain("KMSQA"));
            Assert.That(summary, Does.Contain("NOT uploaded to Jira"));

            var manifestPath = Path.Combine(evidenceRoot, ctx.InvestigationId.ToString("N"), "evidence-manifest.json");
            var manifest = File.ReadAllText(manifestPath);
            using var doc = JsonDocument.Parse(manifest);
            Assert.That(doc.RootElement.GetProperty("ticket").GetString(), Is.EqualTo("VKMS-7"));
            Assert.That(doc.RootElement.GetProperty("environment").GetProperty("id").GetString(), Is.EqualTo("KMSQA"));
            Assert.That(doc.RootElement.GetProperty("uploadedToJira").GetBoolean(), Is.False);
            Assert.That(manifest, Does.Not.Contain("Sup3r-Secret-pw"));
            Assert.That(manifest, Does.Not.Contain("qa_admin"));
        }
        finally
        {
            if (Directory.Exists(evidenceRoot))
                Directory.Delete(evidenceRoot, recursive: true);
        }
    }
}
