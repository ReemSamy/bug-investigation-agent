using BugInvestigationAgent.Core.Authentication;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Environments;
using BugInvestigationAgent.Skills.Browser;

namespace BugInvestigationAgent.Skills.Tests;

public class ReadOnlyRequestPolicyTests
{
    private static AppEnvironment Prod(bool allowConfirmed = false, params string[] allowedWrites) => new()
    {
        Id = "KMSPROD", DisplayName = "KMS Production", BaseUrl = "https://kms.example.test", Type = EnvironmentType.Production,
        Enabled = true, AllowUserConfirmedStateChanges = allowConfirmed,
        Login = new LoginFormOptions { ReadOnlyAllowedWritePaths = allowedWrites.ToList() }.WithDefaults()
    };

    private static RuntimeContext SignedInReadOnly() => new() { IsReadOnlyEnvironment = true, AuthenticationStatus = AuthenticationStatus.Succeeded };

    [TestCase("POST")]
    [TestCase("PUT")]
    [TestCase("PATCH")]
    [TestCase("DELETE")]
    public void Blocks_every_write_method_after_sign_in_on_production(string method)
    {
        var (allowed, reason) = ReadOnlyRequestPolicy.Evaluate(method, "https://kms.example.test/Incident/Save?id=1", SignedInReadOnly(), Prod());
        Assert.That(allowed, Is.False);
        Assert.That(reason, Does.Contain("/Incident/Save").And.Not.Contain("id=1"), "no query strings recorded");
    }

    [TestCase("GET")]
    [TestCase("HEAD")]
    [TestCase("OPTIONS")]
    public void Allows_read_methods(string method) =>
        Assert.That(ReadOnlyRequestPolicy.Evaluate(method, "https://kms.example.test/Incident", SignedInReadOnly(), Prod()).Allowed, Is.True);

    [Test]
    public void Allows_the_login_post_during_sign_in_only()
    {
        var ctx = new RuntimeContext { IsReadOnlyEnvironment = true, AuthenticationStatus = AuthenticationStatus.InProgress };
        Assert.That(ReadOnlyRequestPolicy.Evaluate("POST", "https://kms.example.test/Account/Login", ctx, Prod()).Allowed, Is.True);
        ctx.AuthenticationStatus = AuthenticationStatus.Succeeded;
        Assert.That(ReadOnlyRequestPolicy.Evaluate("POST", "https://kms.example.test/Account/Login", ctx, Prod()).Allowed, Is.False);
    }

    [Test]
    public void User_confirmation_alone_never_lifts_production_protection()
    {
        var ctx = SignedInReadOnly();
        ctx.StateChangingActionsConfirmed = true; // e.g. tampered context or UI tick
        Assert.That(ReadOnlyRequestPolicy.Evaluate("POST", "https://kms.example.test/save", ctx, Prod(allowConfirmed: false)).Allowed, Is.False);
        Assert.That(ReadOnlyRequestPolicy.Evaluate("POST", "https://kms.example.test/save", ctx, Prod(allowConfirmed: true)).Allowed, Is.True);
    }

    [Test]
    public void Only_admin_allow_listed_read_apis_may_use_post()
    {
        var env = Prod(false, "^/api/search$");
        Assert.That(ReadOnlyRequestPolicy.Evaluate("POST", "https://kms.example.test/api/search", SignedInReadOnly(), env).Allowed, Is.True);
        Assert.That(ReadOnlyRequestPolicy.Evaluate("POST", "https://kms.example.test/api/search/delete", SignedInReadOnly(), env).Allowed, Is.False);
    }

    [Test]
    public void Non_read_only_environments_are_not_restricted() =>
        Assert.That(ReadOnlyRequestPolicy.Evaluate("DELETE", "https://kmsqa.example.test/x",
            new RuntimeContext { AuthenticationStatus = AuthenticationStatus.Succeeded }, Prod()).Allowed, Is.True);
}
