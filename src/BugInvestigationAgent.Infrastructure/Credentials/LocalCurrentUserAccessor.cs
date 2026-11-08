using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Credentials;

namespace BugInvestigationAgent.Infrastructure.Credentials;

/// <summary>
/// The app has no end-user authentication of its own; the person using it is identified by the OS account
/// running the app plus the Jira account signed in under Sign in / Account. Saved credentials are isolated per
/// this identity, so switching either the Windows user or the Jira account hides the previous user's profiles.
/// Replace this with a claims-based accessor if the app is ever hosted for multiple users with real sign-in.
/// </summary>
public sealed class LocalCurrentUserAccessor : ICurrentUserAccessor
{
    private readonly IJiraConnectionSettings _jira;

    public LocalCurrentUserAccessor(IJiraConnectionSettings jira) => _jira = jira;

    public string UserId
    {
        get
        {
            var os = $"{System.Environment.UserDomainName}\\{System.Environment.UserName}";
            var jira = _jira.SignedInEmail;
            return string.IsNullOrWhiteSpace(jira)
                ? $"os:{os}".ToLowerInvariant()
                : $"os:{os}|jira:{jira.Trim()}".ToLowerInvariant();
        }
    }
}
