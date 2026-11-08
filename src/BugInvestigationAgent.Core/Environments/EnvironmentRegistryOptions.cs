namespace BugInvestigationAgent.Core.Environments;

/// <summary>
/// Kind of deployed application environment (not an Azure DevOps pipeline stage).
/// </summary>
public enum EnvironmentType
{
    Other = 0,
    Development,
    QA,
    Staging,
    ReleaseCandidate,
    Production
}

/// <summary>
/// Configuration root for the application-environment registry.
/// Bound from the <c>EnvironmentRegistry</c> configuration section. Environments are keyed by their ID so
/// individual values can be overridden without code changes, e.g. via the environment variable
/// <c>EnvironmentRegistry__Environments__KMSRC__BaseUrl</c> or a git-ignored <c>appsettings.Local.json</c>.
/// Never put credentials in this section.
/// </summary>
public sealed class EnvironmentRegistryOptions
{
    public const string SectionName = "EnvironmentRegistry";

    /// <summary>Registered environments keyed by environment ID (e.g. "KMSRC", "KMSQA").</summary>
    public Dictionary<string, EnvironmentDefinitionOptions> Environments { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Allow an "Other — enter URL manually" option for portals that are not registered yet
    /// (keeps the pre-registry workflow available). Ad-hoc environments get their own credential scope per host.
    /// </summary>
    public bool AllowAdHocUrl { get; set; } = true;

    /// <summary>When true, ad-hoc (unregistered) URLs are treated as Production: read-only investigation.</summary>
    public bool TreatAdHocAsProduction { get; set; }

    /// <summary>Login-form detection defaults applied to every environment unless the environment overrides them.</summary>
    public LoginFormOptions DefaultLogin { get; set; } = new();
}

public sealed class EnvironmentDefinitionOptions
{
    public string? DisplayName { get; set; }
    /// <summary>Application/system identifier this environment hosts (e.g. "KMS").</summary>
    public string? ApplicationId { get; set; }
    public string? BaseUrl { get; set; }
    public EnvironmentType Type { get; set; } = EnvironmentType.Other;
    public bool Enabled { get; set; } = true;
    /// <summary>Extra names used to recognise this environment in ticket text (e.g. "KMS RC").</summary>
    public List<string> Aliases { get; set; } = new();
    /// <summary>Optional per-environment login-form overrides (non-empty lists replace the defaults).</summary>
    public LoginFormOptions? Login { get; set; }
    /// <summary>
    /// Exceptional workflows only: allow a user's explicit per-investigation confirmation to lift the read-only guard on
    /// a Production environment. Default false — Production stays read-only regardless of what the user ticks.
    /// </summary>
    public bool AllowUserConfirmedStateChanges { get; set; }
    /// <summary>Optional free-form metadata (region, owner team, notes). Never secrets.</summary>
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// How the agent recognises the login form and the outcome of a login attempt.
/// Lists left empty fall back to generic defaults (see <see cref="WithDefaults"/>);
/// application-specific selectors belong in configuration, not code.
/// </summary>
public sealed class LoginFormOptions
{
    /// <summary>"Form" (default) = username/password form; "None" = the environment requires no login.</summary>
    public string? Mode { get; set; }
    /// <summary>Optional path appended to the Base URL to reach the login page (e.g. "/Account/Login").</summary>
    public string? LoginPath { get; set; }
    public List<string> UsernameSelectors { get; set; } = new();
    public List<string> PasswordSelectors { get; set; } = new();
    public List<string> SubmitSelectors { get; set; } = new();
    /// <summary>Selectors that prove the user is signed in (e.g. a user menu). Strongly recommended.</summary>
    public List<string> SuccessSelectors { get; set; } = new();
    public List<string> ErrorSelectors { get; set; } = new();
    public List<string> MfaSelectors { get; set; } = new();
    public List<string> ErrorTextPatterns { get; set; } = new();
    public List<string> MfaTextPatterns { get; set; } = new();
    public List<string> SessionExpiredTextPatterns { get; set; } = new();
    /// <summary>Extra selectors masked in every screenshot for this environment (sensitive user data).</summary>
    public List<string> MaskSelectors { get; set; } = new();
    public int? OutcomeTimeoutMs { get; set; }
    public int? PollIntervalMs { get; set; }
    /// <summary>
    /// Local development only: treat "login form disappeared without an error" as success when no SuccessSelectors are
    /// configured. Default false — without a success selector the agent reports that the environment needs setup.
    /// </summary>
    public bool? AllowUnverifiedLogin { get; set; }
    /// <summary>Read-only environments: request paths (regex) that may use POST/PUT/PATCH/DELETE because they only read
    /// (e.g. a search API). Everything else that is not GET/HEAD/OPTIONS is blocked after sign-in.</summary>
    public List<string> ReadOnlyAllowedWritePaths { get; set; } = new();

    public bool RequiresLogin => !string.Equals(Mode, "None", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when a successful sign-in can actually be verified for this environment.</summary>
    public bool CanVerifyLogin => !RequiresLogin || SuccessSelectors.Count > 0 || AllowUnverifiedLogin == true;

    /// <summary>Overlay: non-empty values in <paramref name="overrides"/> replace values here.</summary>
    public LoginFormOptions MergeWith(LoginFormOptions? overrides)
    {
        if (overrides is null)
            return Clone();

        return new LoginFormOptions
        {
            Mode = overrides.Mode ?? Mode,
            LoginPath = overrides.LoginPath ?? LoginPath,
            UsernameSelectors = Pick(overrides.UsernameSelectors, UsernameSelectors),
            PasswordSelectors = Pick(overrides.PasswordSelectors, PasswordSelectors),
            SubmitSelectors = Pick(overrides.SubmitSelectors, SubmitSelectors),
            SuccessSelectors = Pick(overrides.SuccessSelectors, SuccessSelectors),
            ErrorSelectors = Pick(overrides.ErrorSelectors, ErrorSelectors),
            MfaSelectors = Pick(overrides.MfaSelectors, MfaSelectors),
            ErrorTextPatterns = Pick(overrides.ErrorTextPatterns, ErrorTextPatterns),
            MfaTextPatterns = Pick(overrides.MfaTextPatterns, MfaTextPatterns),
            SessionExpiredTextPatterns = Pick(overrides.SessionExpiredTextPatterns, SessionExpiredTextPatterns),
            MaskSelectors = MaskSelectors.Concat(overrides.MaskSelectors).Distinct().ToList(),
            OutcomeTimeoutMs = overrides.OutcomeTimeoutMs ?? OutcomeTimeoutMs,
            PollIntervalMs = overrides.PollIntervalMs ?? PollIntervalMs,
            AllowUnverifiedLogin = overrides.AllowUnverifiedLogin ?? AllowUnverifiedLogin,
            ReadOnlyAllowedWritePaths = Pick(overrides.ReadOnlyAllowedWritePaths, ReadOnlyAllowedWritePaths)
        };
    }

    /// <summary>Fills empty lists with generic, application-agnostic defaults.</summary>
    public LoginFormOptions WithDefaults()
    {
        var o = Clone();
        if (o.UsernameSelectors.Count == 0)
            o.UsernameSelectors = ["input[autocomplete='username']", "input[type=email]", "input[name*=user i]", "input[id*=user i]", "input[name*=login i]", "input[type=text]"];
        if (o.PasswordSelectors.Count == 0)
            o.PasswordSelectors = ["input[autocomplete='current-password']", "input[type=password]"];
        if (o.SubmitSelectors.Count == 0)
            o.SubmitSelectors = ["button[type=submit]:visible", "input[type=submit]:visible", "button:has-text('Log in')", "button:has-text('Sign in')", "button:has-text('Login')"];
        if (o.ErrorSelectors.Count == 0)
            o.ErrorSelectors = ["[role=alert]", ".validation-summary-errors", ".field-validation-error", ".error-message", ".alert-danger"];
        if (o.MfaSelectors.Count == 0)
            o.MfaSelectors = ["input[autocomplete='one-time-code']", "input[name*=otp i]", "input[name*=totp i]", "input[name*=verificationcode i]"];
        if (o.ErrorTextPatterns.Count == 0)
            o.ErrorTextPatterns = [@"invalid (user ?name|username|e-?mail|login|credentials|password)", @"incorrect (user ?name|username|password)", @"login (failed|attempt was unsuccessful)", @"wrong (user ?name|password)", @"account (is )?locked"];
        if (o.MfaTextPatterns.Count == 0)
            o.MfaTextPatterns = [@"verification code", @"one-time (pass)?code", @"authenticator app", @"two-factor", @"2-step verification", @"multi-factor", @"approve (the )?sign-?in request"];
        if (o.SessionExpiredTextPatterns.Count == 0)
            o.SessionExpiredTextPatterns = [@"session (has )?expired", @"session timed out", @"please (log|sign) in again"];
        if (o.MaskSelectors.Count == 0)
            o.MaskSelectors = ["input[type=password]", "input[autocomplete*=password]", "input[name*=token i]", "input[name*=secret i]", "[data-sensitive]", "[data-mask]"];
        else
            o.MaskSelectors = new[] { "input[type=password]", "input[autocomplete*=password]" }.Concat(o.MaskSelectors).Distinct().ToList();
        o.OutcomeTimeoutMs ??= 20_000;
        o.PollIntervalMs ??= 500;
        return o;
    }

    public LoginFormOptions Clone() => new()
    {
        Mode = Mode,
        LoginPath = LoginPath,
        UsernameSelectors = UsernameSelectors.ToList(),
        PasswordSelectors = PasswordSelectors.ToList(),
        SubmitSelectors = SubmitSelectors.ToList(),
        SuccessSelectors = SuccessSelectors.ToList(),
        ErrorSelectors = ErrorSelectors.ToList(),
        MfaSelectors = MfaSelectors.ToList(),
        ErrorTextPatterns = ErrorTextPatterns.ToList(),
        MfaTextPatterns = MfaTextPatterns.ToList(),
        SessionExpiredTextPatterns = SessionExpiredTextPatterns.ToList(),
        MaskSelectors = MaskSelectors.ToList(),
        OutcomeTimeoutMs = OutcomeTimeoutMs,
        PollIntervalMs = PollIntervalMs,
        AllowUnverifiedLogin = AllowUnverifiedLogin,
        ReadOnlyAllowedWritePaths = ReadOnlyAllowedWritePaths.ToList()
    };

    private static List<string> Pick(List<string> preferred, List<string> fallback) =>
        preferred.Count > 0 ? preferred.ToList() : fallback.ToList();
}
