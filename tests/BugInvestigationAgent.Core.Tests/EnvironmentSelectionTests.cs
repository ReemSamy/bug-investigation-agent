using BugInvestigationAgent.Core.Authentication;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Environments;

namespace BugInvestigationAgent.Core.Tests;

public class EnvironmentRegistryTests
{
    [Test]
    public void Lists_all_registered_environments_with_their_metadata()
    {
        var registry = new ConfigurationEnvironmentRegistry(TestEnvironments.Options());

        var all = registry.List();

        Assert.That(all.Select(e => e.Id), Is.SupersetOf(new[] { "KMSRC", "KMSQA", "KMSDEV", "KMSOLD", "KMSPROD", "HRQA" }));
        var rc = registry.Get("kmsrc")!; // lookup is case-insensitive
        Assert.That(rc.DisplayName, Is.EqualTo("KMSRC"));
        Assert.That(rc.ApplicationId, Is.EqualTo("KMS"));
        Assert.That(rc.BaseUrl, Is.EqualTo("https://kmsrc.example.test"));
        Assert.That(rc.Type, Is.EqualTo(EnvironmentType.ReleaseCandidate));
        Assert.That(rc.Enabled, Is.True);
        Assert.That(registry.Get("KMSOLD")!.Enabled, Is.False);
    }

    [Test]
    public void Adding_an_environment_is_configuration_only()
    {
        var options = TestEnvironments.Options();
        options.Environments["STAGING2"] = new EnvironmentDefinitionOptions
        {
            ApplicationId = "KMS", BaseUrl = "https://staging2.example.test", Type = EnvironmentType.Staging
        };

        var env = new ConfigurationEnvironmentRegistry(options).Get("STAGING2");

        Assert.That(env, Is.Not.Null);
        Assert.That(env!.DisplayName, Is.EqualTo("STAGING2"), "display name defaults to the id");
        Assert.That(env.Type, Is.EqualTo(EnvironmentType.Staging));
    }

    [Test]
    public void Login_options_merge_environment_overrides_over_defaults()
    {
        var options = TestEnvironments.Options();
        options.DefaultLogin.UsernameSelectors = ["#UserName"];
        options.Environments["KMSQA"].Login = new LoginFormOptions { SuccessSelectors = ["#user-menu"], MaskSelectors = ["#profile-email"] };

        var registry = new ConfigurationEnvironmentRegistry(options);
        var qa = registry.Get("KMSQA")!.Login;
        var rc = registry.Get("KMSRC")!.Login;

        Assert.That(qa.UsernameSelectors, Is.EqualTo(new[] { "#UserName" }));
        Assert.That(qa.SuccessSelectors, Is.EqualTo(new[] { "#user-menu" }));
        Assert.That(qa.MaskSelectors, Does.Contain("#profile-email").And.Contain("input[type=password]"));
        Assert.That(rc.SuccessSelectors, Is.Empty);
        Assert.That(rc.PasswordSelectors, Is.Not.Empty, "generic defaults fill empty lists");
    }

    [Test]
    public void Sanitizer_strips_embedded_credentials_and_query()
    {
        Assert.That(EnvironmentUrlSanitizer.StripCredentials("https://bob:hunter2@kms.example.test/app?token=abc"),
            Is.EqualTo("https://kms.example.test/app"));
        Assert.That(EnvironmentUrlSanitizer.HasEmbeddedCredentials("https://bob:hunter2@kms.example.test"), Is.True);
        Assert.That(EnvironmentUrlSanitizer.HasEmbeddedCredentials("https://kms.example.test"), Is.False);
    }
}

public class EnvironmentInferenceTests
{
    [Test]
    public void Suggests_environment_named_in_ticket_text()
    {
        var ctx = new RuntimeContext { Summary = "Checklist save fails", Description = "Reproduced on KMSQA with admin." };

        var suggestion = TestEnvironments.Selection().Suggest(ctx);

        Assert.That(suggestion.EnvironmentId, Is.EqualTo("KMSQA"));
        Assert.That(suggestion.ApplicationId, Is.EqualTo("KMS"));
    }

    [Test]
    public void Suggests_environment_by_alias()
    {
        var ctx = new RuntimeContext { Description = "Seen on KMS RC after the last deploy." };
        Assert.That(TestEnvironments.Selection().Suggest(ctx).EnvironmentId, Is.EqualTo("KMSRC"));
    }

    [Test]
    public void Suggests_environment_whose_base_url_host_matches_ticket_url()
    {
        var ctx = new RuntimeContext { EnvironmentUrl = "https://kmsrc.example.test/Assignments" };
        var suggestion = TestEnvironments.Selection().Suggest(ctx);
        Assert.That(suggestion.EnvironmentId, Is.EqualTo("KMSRC"));
        Assert.That(suggestion.Reason, Does.Contain("kmsrc.example.test"));
    }

    [Test]
    public void Does_not_guess_when_several_environments_are_mentioned()
    {
        var ctx = new RuntimeContext { Description = "Works on KMSQA but fails on KMSRC." };
        var suggestion = TestEnvironments.Selection().Suggest(ctx);
        Assert.That(suggestion.EnvironmentId, Is.Null);
        Assert.That(suggestion.Reason, Does.Contain("several"));
    }

    [Test]
    public void Url_hosts_do_not_trigger_token_matches()
    {
        // "dev.azure.com" must not be read as an environment called DEV.
        var options = TestEnvironments.Options();
        options.Environments["DEV"] = new EnvironmentDefinitionOptions { BaseUrl = "https://dev.example.test", Type = EnvironmentType.Development };
        var selection = new EnvironmentSelectionService(new ConfigurationEnvironmentRegistry(options));
        var ctx = new RuntimeContext { Description = "Fix PR https://dev.azure.com/org/p/_git/r/pullrequest/1" };

        Assert.That(selection.Suggest(ctx).EnvironmentId, Is.Null);
    }

    [Test]
    public void Disabled_environment_is_not_suggested_and_reason_explains_it()
    {
        var ctx = new RuntimeContext { Description = "Only happens on KMSOLD." };
        var suggestion = TestEnvironments.Selection().Suggest(ctx);
        Assert.That(suggestion.EnvironmentId, Is.Null);
        Assert.That(suggestion.Reason, Does.Contain("disabled"));
    }

    [Test]
    public void Unregistered_portal_url_is_offered_as_adhoc_when_allowed()
    {
        var ctx = new RuntimeContext { EnvironmentUrl = "https://other-portal.example.test" };

        Assert.That(TestEnvironments.Selection(allowAdHoc: true).Suggest(ctx).EnvironmentId, Is.EqualTo(EnvironmentSelectionService.AdHocOptionId));
        Assert.That(TestEnvironments.Selection(allowAdHoc: false).Suggest(ctx).EnvironmentId, Is.Null);
    }

    [Test]
    public void Asks_user_when_environment_cannot_be_determined()
    {
        var ctx = new RuntimeContext { Summary = "Button misaligned" };
        var suggestion = TestEnvironments.Selection().ApplySuggestion(ctx);
        Assert.That(suggestion.EnvironmentId, Is.Null);
        Assert.That(ctx.SuggestedEnvironmentId, Is.Null);
        Assert.That(ctx.SuggestedEnvironmentReason, Does.Contain("Select the environment"));
        Assert.That(ctx.EnvironmentSelectionConfirmed, Is.False, "a suggestion never confirms a selection");
    }
}

public class EnvironmentSelectionValidationTests
{
    [Test]
    public void User_can_override_the_suggestion()
    {
        var selection = TestEnvironments.Selection();
        var ctx = new RuntimeContext { Description = "Seen on KMSQA" };
        selection.ApplySuggestion(ctx);
        Assert.That(ctx.SuggestedEnvironmentId, Is.EqualTo("KMSQA"));

        var result = selection.Select(ctx, "KMSRC");

        Assert.That(result.IsValid, Is.True);
        Assert.That(ctx.SelectedEnvironmentId, Is.EqualTo("KMSRC"));
        Assert.That(ctx.EnvironmentUrl, Is.EqualTo("https://kmsrc.example.test"));
        Assert.That(ctx.EnvironmentSelectionConfirmed, Is.True);
        Assert.That(ctx.IsReadOnlyEnvironment, Is.False);
    }

    [TestCase(null, "Select the environment")]
    [TestCase("NOPE", "not registered")]
    [TestCase("KMSOLD", "disabled")]
    [TestCase("KMSDEV", "no Base URL")]
    public void Invalid_selection_is_explained_and_not_applied(string? id, string expected)
    {
        var selection = TestEnvironments.Selection();
        var ctx = new RuntimeContext();

        var result = selection.Select(ctx, id);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Error, Does.Contain(expected));
        Assert.That(ctx.SelectedEnvironmentId, Is.Null);
        Assert.That(ctx.EnvironmentSelectionConfirmed, Is.False);
        Assert.That(ctx.EnvironmentSelectionError, Does.Contain(expected));
        Assert.That(selection.ResolveSelected(ctx), Is.Null);
    }

    [Test]
    public void Base_url_with_embedded_credentials_is_rejected()
    {
        var options = TestEnvironments.Options();
        options.Environments["BAD"] = new EnvironmentDefinitionOptions { BaseUrl = "https://u:p@bad.example.test" };
        var selection = new EnvironmentSelectionService(new ConfigurationEnvironmentRegistry(options));

        var result = selection.Validate("BAD");

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Error, Does.Contain("credentials"));
    }

    [Test]
    public void Adhoc_url_must_be_a_portal_and_must_not_contain_credentials()
    {
        var selection = TestEnvironments.Selection();
        Assert.That(selection.Validate("adhoc", "https://dev.azure.com/org/p/_git/r/pullrequest/9").IsValid, Is.False);
        Assert.That(selection.Validate("adhoc", "https://bob:pw@portal.example.test").IsValid, Is.False);
        Assert.That(selection.Validate("adhoc", "bob:pw@portal.example.test").IsValid, Is.False);

        var ok = selection.Validate("adhoc", "https://portal.example.test");
        Assert.That(ok.IsValid, Is.True);
        Assert.That(ok.Environment!.Id, Is.EqualTo("adhoc:portal.example.test"));
        Assert.That(ok.Environment.IsAdHoc, Is.True);
    }

    [Test]
    public void Adhoc_url_of_a_registered_host_resolves_to_the_registered_environment()
    {
        var result = TestEnvironments.Selection().Validate("adhoc", "https://kmsqa.example.test/Home");
        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Environment!.Id, Is.EqualTo("KMSQA"));
    }

    [Test]
    public void Production_selection_is_read_only_by_default()
    {
        var ctx = new RuntimeContext();
        TestEnvironments.Selection().Select(ctx, "KMSPROD");
        Assert.That(ctx.IsReadOnlyEnvironment, Is.True);
        Assert.That(ctx.StateChangingActionsConfirmed, Is.False);
    }

    [Test]
    public void Changing_environment_resets_credentials_confirmation_and_authentication()
    {
        var selection = TestEnvironments.Selection();
        var ctx = new RuntimeContext();
        selection.Select(ctx, "KMSPROD");
        ctx.CredentialProfileId = Guid.NewGuid();
        ctx.StateChangingActionsConfirmed = true;
        ctx.AuthenticationStatus = AuthenticationStatus.Succeeded;

        selection.Select(ctx, "KMSQA");

        Assert.That(ctx.CredentialProfileId, Is.Null);
        Assert.That(ctx.StateChangingActionsConfirmed, Is.False);
        Assert.That(ctx.AuthenticationStatus, Is.EqualTo(AuthenticationStatus.NotStarted));
        Assert.That(ctx.IsReadOnlyEnvironment, Is.False);
    }

    [Test]
    public void Environments_for_relevant_application_are_listed_first()
    {
        var list = TestEnvironments.Selection().ListForApplication("HR");
        Assert.That(list[0].ApplicationId, Is.EqualTo("HR"));
        Assert.That(list.Count, Is.EqualTo(6));
    }
}

public class ProductionSafetyPolicyTests
{
    [TestCase("Click Save", true)]
    [TestCase("Create New Draft checklist", true)]
    [TestCase("Promote the checklist", true)]
    [TestCase("Delete the incident", true)]
    [TestCase("Submit the form", true)]
    [TestCase("Open module 'Incident'", false)]
    [TestCase("Open the first incident in the grid", false)]
    [TestCase("Observe that Save shows an error toast", false)]
    [TestCase("Verify the Text Response field is empty", false)]
    [TestCase("Type a partial name into the multi-select filter", false)]
    public void Classifies_potentially_state_changing_steps(string step, bool expected) =>
        Assert.That(ProductionSafetyPolicy.IsPotentiallyStateChanging(step), Is.EqualTo(expected));

    [Test]
    public void Skips_only_on_read_only_environment_without_explicit_confirmation()
    {
        var ctx = new RuntimeContext { IsReadOnlyEnvironment = true };
        Assert.That(ProductionSafetyPolicy.MustSkip(ctx, "Click Save"), Is.True);
        Assert.That(ProductionSafetyPolicy.MustSkip(ctx, "Open module 'Incident'"), Is.False);

        ctx.StateChangingActionsConfirmed = true;
        Assert.That(ProductionSafetyPolicy.MustSkip(ctx, "Click Save"), Is.False);

        var qa = new RuntimeContext { IsReadOnlyEnvironment = false };
        Assert.That(ProductionSafetyPolicy.MustSkip(qa, "Click Save"), Is.False);
    }
}
