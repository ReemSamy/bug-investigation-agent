using System.Text.RegularExpressions;
using BugInvestigationAgent.Core.Authentication;
using BugInvestigationAgent.Core.Credentials;
using BugInvestigationAgent.Core.Environments;

namespace BugInvestigationAgent.Skills.Browser.Authentication;

/// <summary>Minimal page surface the login flow needs. Implemented over Playwright; faked in tests.</summary>
public interface ILoginPage
{
    string Url { get; }
    Task<ILoginField?> FindVisibleAsync(IReadOnlyList<string> selectors, CancellationToken cancellationToken = default);
    Task<bool> AnyVisibleAsync(IReadOnlyList<string> selectors, CancellationToken cancellationToken = default);
    Task<string> GetVisibleTextAsync(CancellationToken cancellationToken = default);
    Task WaitForSettledAsync(CancellationToken cancellationToken = default);
    Task DelayAsync(int milliseconds, CancellationToken cancellationToken = default);
}

public interface ILoginField
{
    Task FillAsync(string value, CancellationToken cancellationToken = default);
    Task ClickAsync(CancellationToken cancellationToken = default);
    Task PressAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// Signs in through an application login form and VERIFIES the outcome — clicking "Log in" is never treated as success.
/// Detects success, invalid credentials, expired sessions and MFA / manual steps from configurable selectors and text
/// patterns (<see cref="LoginFormOptions"/>). Never logs or returns the password.
/// </summary>
public sealed class LoginFlow
{
    public async Task<AuthenticationResult> LoginAsync(
        ILoginPage page,
        LoginFormOptions options,
        CredentialSecret secret,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var o = options.WithDefaults();

        if (!o.RequiresLogin)
        {
            return AuthenticationResult.Of(AuthenticationStatus.Succeeded,
                "This environment is configured to require no login.", SafeUrl(page.Url));
        }

        // Before typing anything: the page may already be an MFA / SSO step or show an expired-session notice.
        var initial = await DetectAsync(page, o, afterSubmit: false, cancellationToken);
        if (initial is not null && initial.Status != AuthenticationStatus.Succeeded)
            return initial;

        var password = await page.FindVisibleAsync(o.PasswordSelectors, cancellationToken);
        var username = await page.FindVisibleAsync(o.UsernameSelectors, cancellationToken);

        if (password is null)
        {
            if (initial?.Status == AuthenticationStatus.Succeeded)
                return initial; // explicit success indicator visible, no login form

            return AuthenticationResult.Of(AuthenticationStatus.ManualInterventionRequired,
                $"No login form was found at {SafeUrl(page.Url)}. The environment may use SSO or a different login page. " +
                "Configure LoginPath / login selectors for this environment, or run the browser headed (Agent:Browser:Headless=false) and complete the sign-in manually, then re-check.",
                SafeUrl(page.Url));
        }

        if (username is null)
        {
            return AuthenticationResult.Of(AuthenticationStatus.ManualInterventionRequired,
                $"A password field was found at {SafeUrl(page.Url)} but no username field. Configure UsernameSelectors for this environment.",
                SafeUrl(page.Url));
        }

        var secretValue = !string.IsNullOrEmpty(secret.Password) ? secret.Password : secret.Token;
        if (string.IsNullOrEmpty(secretValue))
            return AuthenticationResult.Of(AuthenticationStatus.CredentialsRequired, "No password was provided for this environment.");

        progress?.Report($"Submitting the login form as '{secret.Username}'.");
        await username.FillAsync(secret.Username, cancellationToken);
        await password.FillAsync(secretValue, cancellationToken);
        secretValue = null;

        var submit = await page.FindVisibleAsync(o.SubmitSelectors, cancellationToken);
        if (submit is not null)
            await submit.ClickAsync(cancellationToken);
        else
            await password.PressAsync("Enter", cancellationToken);

        progress?.Report("Login submitted — verifying the result (not assuming success).");
        await page.WaitForSettledAsync(cancellationToken);
        return await WaitForOutcomeAsync(page, o, cancellationToken);
    }

    /// <summary>
    /// Types a one-time verification code (relayed by the user through a secure channel) into the open MFA page,
    /// submits it and verifies the outcome. The code is never logged.
    /// </summary>
    public async Task<AuthenticationResult> SubmitMfaCodeAsync(ILoginPage page, LoginFormOptions options, string code, CancellationToken cancellationToken = default)
    {
        var o = options.WithDefaults();
        var otpSelectors = o.MfaSelectors.Concat(["input[autocomplete='one-time-code']", "input[inputmode='numeric']"]).Distinct().ToList();
        var field = await page.FindVisibleAsync(otpSelectors, cancellationToken);
        if (field is null)
            return AuthenticationResult.Of(AuthenticationStatus.ManualInterventionRequired,
                "No verification-code field is visible. Approve the sign-in on your device instead, then re-check.", SafeUrl(page.Url));

        await field.FillAsync(code.Trim(), cancellationToken);
        var submit = await page.FindVisibleAsync(o.SubmitSelectors.Concat(["button:has-text('Verify')", "button:has-text('Continue')"]).ToList(), cancellationToken);
        if (submit is not null)
            await submit.ClickAsync(cancellationToken);
        else
            await field.PressAsync("Enter", cancellationToken);

        await page.WaitForSettledAsync(cancellationToken);
        return await WaitForOutcomeAsync(page, o, cancellationToken);
    }

    /// <summary>Re-evaluates the current page, e.g. after the user completed an MFA step in a headed browser.</summary>
    public async Task<AuthenticationResult> RecheckAsync(ILoginPage page, LoginFormOptions options, CancellationToken cancellationToken = default)
    {
        var o = options.WithDefaults();
        await page.WaitForSettledAsync(cancellationToken);
        return await WaitForOutcomeAsync(page, o, cancellationToken);
    }

    private async Task<AuthenticationResult> WaitForOutcomeAsync(ILoginPage page, LoginFormOptions o, CancellationToken cancellationToken)
    {
        var poll = Math.Max(50, o.PollIntervalMs ?? 500);
        var attempts = Math.Max(1, (o.OutcomeTimeoutMs ?? 20_000) / poll);

        for (var i = 0; i < attempts; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await DetectAsync(page, o, afterSubmit: true, cancellationToken);
            if (outcome is not null)
                return outcome;
            await page.DelayAsync(poll, cancellationToken);
        }

        var stillOnForm = await page.AnyVisibleAsync(o.PasswordSelectors, cancellationToken);
        if (stillOnForm)
        {
            return AuthenticationResult.Of(AuthenticationStatus.InvalidCredentials,
                "The login form is still displayed after submitting — the credentials were not accepted.",
                SafeUrl(page.Url));
        }

        return AuthenticationResult.Of(AuthenticationStatus.Failed,
            o.SuccessSelectors.Count > 0
                ? "The login form went away, but the configured signed-in indicator never appeared. Sign-in could not be verified."
                : "Sign-in could not be verified within the timeout.",
            SafeUrl(page.Url));
    }

    /// <summary>Returns a definitive outcome, or null while the result is still pending.</summary>
    internal async Task<AuthenticationResult?> DetectAsync(ILoginPage page, LoginFormOptions o, bool afterSubmit, CancellationToken cancellationToken)
    {
        var url = SafeUrl(page.Url);
        var text = await SafeTextAsync(page, cancellationToken);

        if (await page.AnyVisibleAsync(o.MfaSelectors, cancellationToken) || MatchesAny(text, o.MfaTextPatterns))
        {
            return AuthenticationResult.Of(AuthenticationStatus.MfaRequired,
                "The environment asks for additional verification (MFA). Complete it in the browser window (requires Agent:Browser:Headless=false), " +
                "then click \"I completed it — re-check sign-in\". Use an account without MFA for fully automated runs.",
                url);
        }

        var passwordVisible = await page.AnyVisibleAsync(o.PasswordSelectors, cancellationToken);

        // A fresh login page may say "please sign in again" — only meaningful once we tried, or with no form to use.
        if ((afterSubmit || !passwordVisible) && MatchesAny(text, o.SessionExpiredTextPatterns))
        {
            return AuthenticationResult.Of(AuthenticationStatus.SessionExpired,
                "The environment reports that the session expired. Sign in again.", url);
        }

        if (afterSubmit && passwordVisible &&
            (MatchesAny(text, o.ErrorTextPatterns) || await page.AnyVisibleAsync(o.ErrorSelectors, cancellationToken)))
        {
            return AuthenticationResult.Of(AuthenticationStatus.InvalidCredentials,
                "The environment rejected the username or password.", url);
        }

        if (o.SuccessSelectors.Count > 0)
        {
            if (!passwordVisible && await page.AnyVisibleAsync(o.SuccessSelectors, cancellationToken))
                return AuthenticationResult.Of(AuthenticationStatus.Succeeded, "Signed-in indicator is visible.", url);
            return null;
        }

        if (afterSubmit && !passwordVisible)
        {
            return AuthenticationResult.Of(AuthenticationStatus.Succeeded,
                "Login form was dismissed with no error, MFA or session message shown.", url);
        }

        return null;
    }

    private static async Task<string> SafeTextAsync(ILoginPage page, CancellationToken cancellationToken)
    {
        try { return await page.GetVisibleTextAsync(cancellationToken) ?? ""; }
        catch (OperationCanceledException) { throw; }
        catch { return ""; }
    }

    private static bool MatchesAny(string text, IEnumerable<string> patterns) =>
        !string.IsNullOrWhiteSpace(text) &&
        patterns.Any(p => !string.IsNullOrWhiteSpace(p) && Regex.IsMatch(text, p, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)));

    private static string? SafeUrl(string? url) => EnvironmentUrlSanitizer.StripCredentials(url);
}
