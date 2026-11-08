using BugInvestigationAgent.Core.Authentication;
using BugInvestigationAgent.Core.Credentials;
using BugInvestigationAgent.Core.Environments;
using BugInvestigationAgent.Skills.Browser.Authentication;

namespace BugInvestigationAgent.Skills.Tests;

/// <summary>Scripted, in-memory login page — no browser, no network, no real environment.</summary>
internal sealed class FakeLoginPage : ILoginPage
{
    private readonly HashSet<string> _visible;

    public FakeLoginPage(params string[] visibleSelectors) => _visible = new HashSet<string>(visibleSelectors);

    public string Url { get; set; } = "https://app.example.test/login";
    public string Text { get; set; } = "Sign in";
    public Dictionary<string, string> Filled { get; } = new();
    public bool Submitted { get; private set; }
    public bool PressedEnter { get; private set; }
    /// <summary>Applied when the form is submitted (click or Enter) — simulates the server response.</summary>
    public Action<FakeLoginPage>? OnSubmit { get; set; }

    public void Show(params string[] selectors) { foreach (var s in selectors) _visible.Add(s); }
    public void Hide(params string[] selectors) { foreach (var s in selectors) _visible.Remove(s); }

    public Task<ILoginField?> FindVisibleAsync(IReadOnlyList<string> selectors, CancellationToken cancellationToken = default)
    {
        var match = selectors.FirstOrDefault(_visible.Contains);
        return Task.FromResult<ILoginField?>(match is null ? null : new Field(this, match));
    }

    public Task<bool> AnyVisibleAsync(IReadOnlyList<string> selectors, CancellationToken cancellationToken = default) =>
        Task.FromResult(selectors.Any(_visible.Contains));

    public Task<string> GetVisibleTextAsync(CancellationToken cancellationToken = default) => Task.FromResult(Text);
    public Task WaitForSettledAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DelayAsync(int milliseconds, CancellationToken cancellationToken = default) => Task.CompletedTask;

    private sealed class Field : ILoginField
    {
        private readonly FakeLoginPage _page;
        private readonly string _selector;
        public Field(FakeLoginPage page, string selector) { _page = page; _selector = selector; }

        public Task FillAsync(string value, CancellationToken cancellationToken = default)
        {
            _page.Filled[_selector] = value;
            return Task.CompletedTask;
        }

        public Task ClickAsync(CancellationToken cancellationToken = default)
        {
            _page.Submitted = true;
            _page.OnSubmit?.Invoke(_page);
            return Task.CompletedTask;
        }

        public Task PressAsync(string key, CancellationToken cancellationToken = default)
        {
            _page.PressedEnter = key == "Enter";
            _page.Submitted = true;
            _page.OnSubmit?.Invoke(_page);
            return Task.CompletedTask;
        }
    }
}

public class LoginFlowTests
{
    private const string User = "#user";
    private const string Pass = "#pass";
    private const string Submit = "#submit";

    private static LoginFormOptions Options(Action<LoginFormOptions>? configure = null)
    {
        var o = new LoginFormOptions
        {
            UsernameSelectors = [User],
            PasswordSelectors = [Pass],
            SubmitSelectors = [Submit],
            ErrorSelectors = ["#error"],
            MfaSelectors = ["#otp"],
            OutcomeTimeoutMs = 1_000,
            PollIntervalMs = 100
        };
        configure?.Invoke(o);
        return o;
    }

    private static readonly CredentialSecret Secret = new() { Username = "qa_admin", Password = "S3cret!pw" };

    [Test]
    public async Task Without_a_success_selector_login_is_reported_as_unverifiable_not_successful()
    {
        var page = new FakeLoginPage(User, Pass, Submit)
        {
            OnSubmit = p => { p.Hide(User, Pass, Submit); p.Text = "Welcome"; }
        };

        var result = await new LoginFlow().LoginAsync(page, Options(), Secret);

        Assert.That(result.Status, Is.EqualTo(AuthenticationStatus.VerificationNotConfigured));
        Assert.That(result.Message, Does.Contain("SuccessSelectors"));
        Assert.That(Options().WithDefaults().CanVerifyLogin, Is.False);
        Assert.That(Options(o => o.SuccessSelectors = ["#user-menu"]).WithDefaults().CanVerifyLogin, Is.True);
    }

    [Test]
    public async Task Heuristic_success_only_with_explicit_local_opt_in()
    {
        var page = new FakeLoginPage(User, Pass, Submit)
        {
            OnSubmit = p => { p.Hide(User, Pass, Submit); p.Text = "Welcome, My Assignments"; p.Url = "https://app.example.test/Home"; }
        };

        var result = await new LoginFlow().LoginAsync(page, Options(o => o.AllowUnverifiedLogin = true), Secret);
        Assert.That(result.Message, Does.Contain("UNVERIFIED"));

        Assert.That(result.Status, Is.EqualTo(AuthenticationStatus.Succeeded));
        Assert.That(page.Filled[User], Is.EqualTo("qa_admin"));
        Assert.That(page.Filled[Pass], Is.EqualTo("S3cret!pw"));
        Assert.That(result.Message, Does.Not.Contain("S3cret!pw"));
    }

    [Test]
    public async Task Clicking_login_is_not_success_when_form_stays_with_error()
    {
        var page = new FakeLoginPage(User, Pass, Submit)
        {
            OnSubmit = p => { p.Text = "Invalid username or password."; }
        };

        var result = await new LoginFlow().LoginAsync(page, Options(), Secret);

        Assert.That(page.Submitted, Is.True);
        Assert.That(result.Status, Is.EqualTo(AuthenticationStatus.InvalidCredentials));
    }

    [Test]
    public async Task Form_still_displayed_after_timeout_is_treated_as_rejected()
    {
        var page = new FakeLoginPage(User, Pass, Submit); // nothing changes after submit

        var result = await new LoginFlow().LoginAsync(page, Options(), Secret);

        Assert.That(result.Status, Is.EqualTo(AuthenticationStatus.InvalidCredentials));
        Assert.That(result.Message, Does.Contain("still displayed"));
    }

    [Test]
    public async Task Error_element_with_visible_form_is_invalid_credentials()
    {
        var page = new FakeLoginPage(User, Pass, Submit) { OnSubmit = p => p.Show("#error") };
        var result = await new LoginFlow().LoginAsync(page, Options(), Secret);
        Assert.That(result.Status, Is.EqualTo(AuthenticationStatus.InvalidCredentials));
    }

    [Test]
    public async Task Mfa_prompt_after_submit_pauses_for_user_action()
    {
        var page = new FakeLoginPage(User, Pass, Submit)
        {
            OnSubmit = p => { p.Hide(User, Pass, Submit); p.Show("#otp"); p.Text = "Enter the verification code we sent"; }
        };

        var result = await new LoginFlow().LoginAsync(page, Options(), Secret);

        Assert.That(result.Status, Is.EqualTo(AuthenticationStatus.MfaRequired));
        Assert.That(result.NeedsUserAction, Is.True);
    }

    [Test]
    public async Task Mfa_detected_from_text_patterns_only()
    {
        var page = new FakeLoginPage(User, Pass, Submit)
        {
            OnSubmit = p => { p.Hide(User, Pass, Submit); p.Text = "Approve the sign-in request in your Authenticator app"; }
        };
        var result = await new LoginFlow().LoginAsync(page, Options(), Secret);
        Assert.That(result.Status, Is.EqualTo(AuthenticationStatus.MfaRequired));
    }

    [Test]
    public async Task Recheck_after_manual_step_succeeds_once_mfa_is_gone()
    {
        var page = new FakeLoginPage("#otp") { Text = "Enter verification code" };
        var flow = new LoginFlow();

        Assert.That((await flow.RecheckAsync(page, Options())).Status, Is.EqualTo(AuthenticationStatus.MfaRequired));

        page.Hide("#otp");
        page.Show("#user-menu");
        page.Text = "Dashboard";
        Assert.That((await flow.RecheckAsync(page, Options(o => o.SuccessSelectors = ["#user-menu"]))).Status, Is.EqualTo(AuthenticationStatus.Succeeded));
    }

    [Test]
    public async Task Session_expired_message_after_submit_is_reported()
    {
        var page = new FakeLoginPage(User, Pass, Submit)
        {
            OnSubmit = p => { p.Hide(User, Pass, Submit); p.Text = "Your session has expired."; }
        };
        var result = await new LoginFlow().LoginAsync(page, Options(), Secret);
        Assert.That(result.Status, Is.EqualTo(AuthenticationStatus.SessionExpired));
    }

    [Test]
    public async Task No_login_form_requires_manual_intervention_instead_of_assuming_success()
    {
        var page = new FakeLoginPage { Text = "Redirecting to your identity provider…" };
        var result = await new LoginFlow().LoginAsync(page, Options(), Secret);
        Assert.That(result.Status, Is.EqualTo(AuthenticationStatus.ManualInterventionRequired));
        Assert.That(page.Filled, Is.Empty);
    }

    [Test]
    public async Task Configured_success_indicator_must_appear()
    {
        var options = Options(o => o.SuccessSelectors = ["#user-menu"]);

        var noIndicator = new FakeLoginPage(User, Pass, Submit) { OnSubmit = p => p.Hide(User, Pass, Submit) };
        Assert.That((await new LoginFlow().LoginAsync(noIndicator, options, Secret)).Status, Is.EqualTo(AuthenticationStatus.Failed));

        var withIndicator = new FakeLoginPage(User, Pass, Submit) { OnSubmit = p => { p.Hide(User, Pass, Submit); p.Show("#user-menu"); } };
        Assert.That((await new LoginFlow().LoginAsync(withIndicator, options, Secret)).Status, Is.EqualTo(AuthenticationStatus.Succeeded));
    }

    [Test]
    public async Task Presses_enter_when_no_submit_button_is_found()
    {
        var page = new FakeLoginPage(User, Pass) { OnSubmit = p => { p.Hide(User, Pass); p.Show("#user-menu"); } };
        var result = await new LoginFlow().LoginAsync(page, Options(o => o.SuccessSelectors = ["#user-menu"]), Secret);
        Assert.That(page.PressedEnter, Is.True);
        Assert.That(result.Status, Is.EqualTo(AuthenticationStatus.Succeeded));
    }

    [Test]
    public async Task Environment_configured_without_login_succeeds_without_typing()
    {
        var page = new FakeLoginPage(User, Pass);
        var result = await new LoginFlow().LoginAsync(page, Options(o => o.Mode = "None"), new CredentialSecret { Username = "" });
        Assert.That(result.Status, Is.EqualTo(AuthenticationStatus.Succeeded));
        Assert.That(page.Filled, Is.Empty);
    }

    [Test]
    public async Task Progress_messages_never_contain_the_password()
    {
        var messages = new List<string>();
        var page = new FakeLoginPage(User, Pass, Submit) { OnSubmit = p => p.Hide(User, Pass, Submit) };

        await new LoginFlow().LoginAsync(page, Options(), Secret, new SyncProgress(messages));

        Assert.That(messages, Is.Not.Empty);
        Assert.That(messages.Any(m => m.Contains("S3cret!pw")), Is.False);
    }

    private sealed class SyncProgress : IProgress<string>
    {
        private readonly List<string> _sink;
        public SyncProgress(List<string> sink) => _sink = sink;
        public void Report(string value) => _sink.Add(value);
    }
}
