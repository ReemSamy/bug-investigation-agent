using System.Collections.Concurrent;
using AI.BugInvestigationTool.Configuration;

namespace AI.BugInvestigationTool.Services.Workflow;

/// <summary>
/// UI helper: keeps the credentials the user typed in memory (never on disk) and runs investigations
/// in the background so pages can show progress. It delegates all work to <see cref="InvestigationEngine"/>.
/// </summary>
public sealed class InvestigationRunner(IServiceScopeFactory scopes, ILogger<InvestigationRunner> logger)
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
    private readonly object _credLock = new();
    private Credentials _credentials = new();

    public Credentials GetCredentials() { lock (_credLock) return _credentials.Clone(); }
    public void SetCredentials(Credentials credentials) { lock (_credLock) _credentials = credentials.Clone(); }

    public bool IsRunning(string id) => _running.ContainsKey(id);

    public bool Start(string id, IDictionary<string, string>? userInfo)
    {
        var cts = new CancellationTokenSource();
        if (!_running.TryAdd(id, cts)) return false;
        var creds = GetCredentials();
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                var engine = scope.ServiceProvider.GetRequiredService<InvestigationEngine>();
                await engine.RunAsync(id, creds, userInfo, cts.Token);
            }
            catch (Exception ex)
            {
                logger.LogError("Background investigation {Id} crashed: {Type}", id, ex.GetType().Name);
            }
            finally
            {
                _running.TryRemove(id, out _);
                cts.Dispose();
            }
        });
        return true;
    }

    public bool Cancel(string id)
    {
        if (!_running.TryGetValue(id, out var cts)) return false;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }
}
