namespace BugInvestigationAgent.Core.Authentication;

/// <summary>Outcome of signing in to the selected application environment.</summary>
public enum AuthenticationStatus
{
    NotStarted = 0,
    /// <summary>No credentials are available for the selected environment in this investigation.</summary>
    CredentialsRequired,
    InProgress,
    Succeeded,
    InvalidCredentials,
    SessionExpired,
    /// <summary>The application asked for a second factor (code, authenticator approval…).</summary>
    MfaRequired,
    /// <summary>Some other step needs a human (SSO redirect, unknown login form, consent page…).</summary>
    ManualInterventionRequired,
    Failed,
    /// <summary>The login form went away, but the environment has no success selector configured, so sign-in cannot be verified.</summary>
    VerificationNotConfigured
}

public sealed class AuthenticationResult
{
    public required AuthenticationStatus Status { get; init; }
    public required string Message { get; init; }
    /// <summary>Page URL after the attempt (credentials and query string removed).</summary>
    public string? LandingUrl { get; init; }

    public bool Succeeded => Status == AuthenticationStatus.Succeeded;
    public bool NeedsUserAction => Status is AuthenticationStatus.MfaRequired or AuthenticationStatus.ManualInterventionRequired;

    public static AuthenticationResult Of(AuthenticationStatus status, string message, string? landingUrl = null) =>
        new() { Status = status, Message = message, LandingUrl = landingUrl };
}

public static class AuthenticationStatusDisplay
{
    public static string Label(AuthenticationStatus status) => status switch
    {
        AuthenticationStatus.NotStarted => "Not signed in yet",
        AuthenticationStatus.CredentialsRequired => "Credentials required",
        AuthenticationStatus.InProgress => "Signing in…",
        AuthenticationStatus.Succeeded => "Signed in",
        AuthenticationStatus.InvalidCredentials => "Sign-in failed — invalid credentials",
        AuthenticationStatus.SessionExpired => "Session expired",
        AuthenticationStatus.MfaRequired => "Additional verification (MFA) required",
        AuthenticationStatus.ManualInterventionRequired => "Manual action required",
        AuthenticationStatus.Failed => "Sign-in failed",
        AuthenticationStatus.VerificationNotConfigured => "Sign-in not verifiable — environment needs setup",
        _ => status.ToString()
    };
}
