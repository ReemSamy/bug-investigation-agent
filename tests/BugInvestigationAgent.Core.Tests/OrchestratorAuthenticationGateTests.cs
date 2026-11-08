using System.Collections.Concurrent;
using BugInvestigationAgent.Core.Authentication;
using BugInvestigationAgent.Core.Browser;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Credentials;
using BugInvestigationAgent.Core.Environments;
using BugInvestigationAgent.Core.Memory;
using BugInvestigationAgent.Core.Orchestration;
using BugInvestigationAgent.Core.Permissions;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Core.Sessions;
using BugInvestigationAgent.Core.Skills;
using BugInvestigationAgent.Core.Workflow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BugInvestigationAgent.Core.Tests;

/// <summary>
/// End-to-end orchestration with fake skills (no real browser, no real environments):
/// reproduction must never start before a verified sign-in to the selected environment.
/// </summary>
public class OrchestratorAuthenticationGateTests
{
    private sealed class FakeSkill : ISkill
    {
        private readonly Func<SkillExecutionRequest, Task> _run;
        public FakeSkill(string id, Func<SkillExecutionRequest, Task>? run = null)
        {
            SkillId = id;
            _run = run ?? (_ => Task.CompletedTask);
        }
        public string SkillId { get; }
        public string DisplayName => SkillId;
        public AgentPermission RequiredPermissions => AgentPermission.None;
        public bool CanExecute(RuntimeContext context, IPermissionGate permissions) => true;
        public async Task<SkillResult> ExecuteAsync(SkillExecutionRequest request, CancellationToken cancellationToken = default)
        {
            await _run(request);
            return SkillResult.Ok();
        }
    }

    private sealed class Harness
    {
        public readonly InMemoryAgentSessionStore Sessions = new();
        public readonly InMemorySessionCredentialStore Credentials = new();
        public readonly EnvironmentSelectionService Environments = TestEnvironments.Selection();
        public readonly Mock<ICredentialVault> Vault = new();
        public readonly Mock<IBrowserSessionCloser> BrowserCloser = new();
        public readonly ConcurrentQueue<AuthenticationStatus> LoginOutcomes = new();
        public readonly List<(string Mode, string? EnvironmentId, string? Username)> LoginAttempts = new();
        public int ReproductionRuns;
        public AuthenticationStatus StatusWhenReproductionStarted;
        public AgentOrchestrator Orchestrator = null!;

        public Harness(string ticketText)
        {
            Vault.Setup(v => v.FindForEnvironmentAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((CredentialProfile?)null);
            Vault.Setup(v => v.SaveProfileAsync(It.IsAny<CredentialProfile>(), It.IsAny<CredentialSecret>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((CredentialProfile p, CredentialSecret _, CancellationToken _) => p);

            var services = new ServiceCollection();
            services.AddScoped<ISkill>(_ => new FakeSkill(SkillIds.Jira, r =>
            {
                r.Context.JiraDetailsLoaded = true;
                r.Context.Summary = "Checklist text response missing";
                r.Context.Description = ticketText;
                r.Context.ReproductionSteps = new List<string> { "Open module 'Incident'", "Observe the checklist" };
                return Task.CompletedTask;
            }));
            foreach (var id in new[] { SkillIds.TicketUnderstanding, SkillIds.HistoricalJiraSearch, SkillIds.HistoricalReproductionReuse, SkillIds.KmsDocumentation, SkillIds.EvidenceReporting })
                services.AddScoped<ISkill>(_ => new FakeSkill(id));
            services.AddScoped<ISkill>(_ => new FakeSkill(SkillIds.EnvironmentDiscovery, r =>
            {
                Environments.ApplySuggestion(r.Context);
                return Task.CompletedTask;
            }));
            services.AddScoped<ISkill>(_ => new FakeSkill(SkillIds.BrowserAutomation, r =>
            {
                var ctx = r.Context;
                var mode = r.Inputs?["mode"] ?? "login";
                var env = ctx.SelectedEnvironmentId;
                var secret = mode == "recheck" ? null : Credentials.Take(ctx.InvestigationId, env!);
                lock (LoginAttempts)
                    LoginAttempts.Add((mode, env, secret?.Username));
                if (mode != "recheck" && secret is null)
                {
                    ctx.AuthenticationStatus = AuthenticationStatus.CredentialsRequired;
                    ctx.AuthenticationMessage = "Credentials required.";
                    return Task.CompletedTask;
                }
                LoginOutcomes.TryDequeue(out var outcome);
                ctx.AuthenticationStatus = outcome;
                ctx.AuthenticationMessage = outcome switch
                {
                    AuthenticationStatus.InvalidCredentials => "The environment rejected the username or password.",
                    AuthenticationStatus.MfaRequired => "Enter the verification code.",
                    _ => "ok"
                };
                return Task.CompletedTask;
            }));
            services.AddScoped<ISkill>(_ => new FakeSkill(SkillIds.Reproduction, r =>
            {
                Interlocked.Increment(ref ReproductionRuns);
                StatusWhenReproductionStarted = r.Context.AuthenticationStatus;
                r.Context.ReproductionOutcome = ReproductionOutcome.NotReproduced;
                return Task.CompletedTask;
            }));
            services.AddScoped(_ => Vault.Object);
            services.AddScoped(_ => Mock.Of<IEngineeringMemoryStore>());
            var provider = services.BuildServiceProvider();

            var resolver = new MissingInfoResolver(new FixedWorkspace(), new SignedInJira(), Environments, Credentials);
            Orchestrator = new AgentOrchestrator(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Sessions,
                new SecretRedactor(),
                resolver,
                new PermissionGate((AgentPermission)int.MaxValue),
                new FixedWorkspace(),
                BrowserCloser.Object,
                Environments,
                Credentials,
                NullLogger<AgentOrchestrator>.Instance);
        }

        public int Attempts { get { lock (LoginAttempts) return LoginAttempts.Count; } }
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Timed out waiting for: " + what);
            await Task.Delay(20);
        }
    }

    private static Dictionary<string, string> Env(string id) => new() { ["environmentId"] = id };
    private static CredentialSecret Creds(string user, string pw) => new() { Username = user, Password = pw };

    [Test]
    public async Task Reproduction_waits_for_successful_authentication_with_retry_after_invalid_credentials()
    {
        var h = new Harness("Reproduced on KMSQA by the customer.");
        h.LoginOutcomes.Enqueue(AuthenticationStatus.InvalidCredentials);
        h.LoginOutcomes.Enqueue(AuthenticationStatus.Succeeded);
        var session = h.Sessions.Create();

        await h.Orchestrator.StartAsync(session.Id, "VKMS-1", null);
        await WaitUntil(() => session.State == AgentState.AwaitingMissingInfo, "initial missing info");

        // Environment inferred from the ticket is only a suggestion.
        Assert.That(session.Context.SuggestedEnvironmentId, Is.EqualTo("KMSQA"));
        Assert.That(session.Context.EnvironmentSelectionConfirmed, Is.False);
        Assert.That(session.Context.MissingFields.Select(f => f.Key), Does.Contain("environment").And.Contain("credentials"));

        // User overrides the suggestion and enters wrong credentials (not remembered).
        await h.Orchestrator.SubmitMissingInfoAsync(session.Id, Env("KMSRC"), Creds("rc_admin", "wrong"), saveCredentialProfile: false);
        await WaitUntil(() => h.Attempts == 1 && session.State == AgentState.AwaitingMissingInfo, "failed sign-in prompt");

        Assert.That(h.ReproductionRuns, Is.EqualTo(0), "no reproduction after failed sign-in");
        Assert.That(session.Context.AuthenticationStatus, Is.EqualTo(AuthenticationStatus.InvalidCredentials));
        Assert.That(session.Context.MissingFields.Any(f => f.Key == "credentials"), Is.True);
        Assert.That(session.Context.MissingInfoReason, Does.Contain("rejected"));
        Assert.That(h.Credentials.Has(session.Id, "KMSRC"), Is.False, "secret consumed by the attempt");

        await h.Orchestrator.SubmitMissingInfoAsync(session.Id, Env("KMSRC"), Creds("rc_admin", "right"), saveCredentialProfile: false);
        await WaitUntil(() => session.State == AgentState.Completed, "completion");

        Assert.That(h.ReproductionRuns, Is.EqualTo(1));
        Assert.That(h.StatusWhenReproductionStarted, Is.EqualTo(AuthenticationStatus.Succeeded));
        Assert.That(session.Context.SelectedEnvironmentId, Is.EqualTo("KMSRC"), "user override wins over the suggestion");
        Assert.That(h.LoginAttempts.All(a => a.EnvironmentId == "KMSRC" && a.Username == "rc_admin"), Is.True);
        h.Vault.Verify(v => v.SaveProfileAsync(It.IsAny<CredentialProfile>(), It.IsAny<CredentialSecret>(), It.IsAny<CancellationToken>()),
            Times.Never, "credentials are not persisted without explicit opt-in");
        Assert.That(session.Context.StatusLog.Any(l => l.Contains("wrong") || l.Contains("right")), Is.False, "passwords never logged");
    }

    [Test]
    public async Task Mfa_pauses_until_user_completes_it_then_rechecks_the_same_session()
    {
        var h = new Harness("Seen on KMSQA");
        h.LoginOutcomes.Enqueue(AuthenticationStatus.MfaRequired);
        h.LoginOutcomes.Enqueue(AuthenticationStatus.Succeeded);
        var session = h.Sessions.Create();

        await h.Orchestrator.StartAsync(session.Id, "VKMS-2", null);
        await WaitUntil(() => session.State == AgentState.AwaitingMissingInfo, "initial missing info");
        await h.Orchestrator.SubmitMissingInfoAsync(session.Id, Env("KMSQA"), Creds("qa_admin", "pw"), false);
        await WaitUntil(() => h.Attempts == 1 && session.State == AgentState.AwaitingMissingInfo, "MFA prompt");

        Assert.That(session.Context.AuthenticationStatus, Is.EqualTo(AuthenticationStatus.MfaRequired));
        Assert.That(session.Context.MissingFields.Single().InputType, Is.EqualTo("auth-manual"));
        Assert.That(h.ReproductionRuns, Is.EqualTo(0));
        // Only the close on initial environment selection (before login) — the MFA browser stays open.
        h.BrowserCloser.Verify(b => b.CloseAsync(session.Id), Times.Once, "browser stays open so the MFA step can be completed");

        await h.Orchestrator.SubmitMissingInfoAsync(session.Id, new Dictionary<string, string> { ["authAction"] = "recheck" }, null, false);
        await WaitUntil(() => session.State == AgentState.Completed, "completion");

        Assert.That(h.LoginAttempts[1].Mode, Is.EqualTo("recheck"));
        Assert.That(h.ReproductionRuns, Is.EqualTo(1));
    }

    [Test]
    public async Task Environment_without_url_never_starts_browser_or_reproduction()
    {
        var h = new Harness("No environment mentioned");
        var session = h.Sessions.Create();

        await h.Orchestrator.StartAsync(session.Id, "VKMS-3", null);
        await WaitUntil(() => session.State == AgentState.AwaitingMissingInfo, "initial missing info");
        Assert.That(session.Context.SuggestedEnvironmentId, Is.Null);

        await h.Orchestrator.SubmitMissingInfoAsync(session.Id, Env("KMSDEV"), Creds("dev", "pw"), false);
        await Task.Delay(200);

        Assert.That(session.State, Is.EqualTo(AgentState.AwaitingMissingInfo));
        Assert.That(session.Context.MissingInfoReason, Does.Contain("no Base URL"));
        Assert.That(h.Attempts, Is.EqualTo(0));
        Assert.That(h.ReproductionRuns, Is.EqualTo(0));
        Assert.That(h.Credentials.Has(session.Id, "KMSDEV"), Is.False, "credentials are not accepted for an unusable environment");
    }

    [Test]
    public async Task Remember_credentials_persists_only_for_the_selected_environment_when_opted_in()
    {
        var h = new Harness("Seen on KMSQA");
        h.LoginOutcomes.Enqueue(AuthenticationStatus.Succeeded);
        var session = h.Sessions.Create();

        await h.Orchestrator.StartAsync(session.Id, "VKMS-4", null);
        await WaitUntil(() => session.State == AgentState.AwaitingMissingInfo, "initial missing info");
        await h.Orchestrator.SubmitMissingInfoAsync(session.Id, Env("KMSQA"), Creds("qa_admin", "pw"), saveCredentialProfile: true);
        await WaitUntil(() => session.State == AgentState.Completed, "completion");

        h.Vault.Verify(v => v.SaveProfileAsync(
            It.Is<CredentialProfile>(p => p.EnvironmentId == "KMSQA" && p.Username == "qa_admin"),
            It.Is<CredentialSecret>(s => s.Password == "pw"),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.That(h.Credentials.Has(session.Id, "KMSQA"), Is.False, "in-memory secret cleared after the run");
    }

    [Test]
    public async Task Production_selection_marks_investigation_read_only_unless_explicitly_confirmed()
    {
        var h = new Harness("Seen in production");
        h.LoginOutcomes.Enqueue(AuthenticationStatus.Succeeded);
        var session = h.Sessions.Create();

        await h.Orchestrator.StartAsync(session.Id, "VKMS-5", null);
        await WaitUntil(() => session.State == AgentState.AwaitingMissingInfo, "initial missing info");
        await h.Orchestrator.SubmitMissingInfoAsync(session.Id, Env("KMSPROD"), Creds("viewer", "pw"), false);
        await WaitUntil(() => session.State == AgentState.Completed, "completion");

        Assert.That(session.Context.IsReadOnlyEnvironment, Is.True);
        Assert.That(session.Context.StateChangingActionsConfirmed, Is.False);
    }
}
