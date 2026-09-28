namespace BugInvestigationAgent.Core.Browser;

/// <summary>
/// Abstraction over browser automation backends.
/// Deterministic Playwright remains the default; MCP-backed adaptive control is optional.
/// </summary>
public interface IBrowserToolSurface
{
    string BackendId { get; }
    bool IsAvailable { get; }

    Task NavigateAsync(string url, CancellationToken cancellationToken = default);
    Task<string> SnapshotAsync(CancellationToken cancellationToken = default);
    Task ClickAsync(string targetDescription, CancellationToken cancellationToken = default);
    Task<string> TakeScreenshotAsync(string path, CancellationToken cancellationToken = default);
}
