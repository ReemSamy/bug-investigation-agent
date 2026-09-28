namespace BugInvestigationAgent.Core.Browser;

/// <summary>
/// Closes an active automation browser for a workflow session (login + reproduce share one browser).
/// </summary>
public interface IBrowserSessionCloser
{
    Task CloseAsync(Guid sessionId);
}
