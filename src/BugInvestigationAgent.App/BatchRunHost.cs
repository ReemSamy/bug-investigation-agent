using BugInvestigationAgent.Core.Investigations;

namespace BugInvestigationAgent.App;

/// <summary>
/// Runs a legacy batch in the background of the local app so the page can be closed. Started and stopped only
/// by explicit clicks; nothing runs automatically at startup (progress is persisted, so Resume continues it).
/// </summary>
public sealed class BatchRunHost
{
    private readonly LegacyBatchService _service;
    private readonly ILogger<BatchRunHost> _logger;
    private readonly Dictionary<Guid, CancellationTokenSource> _runs = new();
    private readonly object _gate = new();

    public BatchRunHost(LegacyBatchService service, ILogger<BatchRunHost> logger)
    {
        _service = service;
        _logger = logger;
    }

    public string? LastError { get; private set; }

    public bool IsRunning(Guid batchId) => _service.IsRunning(batchId);

    public bool Start(Guid batchId, bool retryUnsuccessful, int? maxItems, bool rerunInvestigated = false)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_runs.ContainsKey(batchId) || _service.IsRunning(batchId))
                return false;
            cts = new CancellationTokenSource();
            _runs[batchId] = cts;
        }
        LastError = null;
        _ = Task.Run(async () =>
        {
            try
            {
                await _service.RunAsync(batchId, retryUnsuccessful, maxItems, cts.Token, rerunInvestigated);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _logger.LogWarning("Batch {Batch} stopped: {Error}", batchId, ex.Message);
            }
            finally
            {
                lock (_gate)
                    _runs.Remove(batchId);
                cts.Dispose();
            }
        });
        return true;
    }

    public void Stop(Guid batchId)
    {
        lock (_gate)
        {
            if (_runs.TryGetValue(batchId, out var cts))
                cts.Cancel();
        }
    }
}
