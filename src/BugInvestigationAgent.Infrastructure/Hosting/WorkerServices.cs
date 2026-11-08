using BugInvestigationAgent.Core.Jobs;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Infrastructure.Forge;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BugInvestigationAgent.Infrastructure.Hosting;

/// <summary>Pulls job ids from the queue and runs up to MaxConcurrentJobs investigations concurrently.</summary>
public sealed class InvestigationWorkerService : BackgroundService
{
    private readonly IInvestigationQueue _queue;
    private readonly InvestigationJobRunner _runner;
    private readonly WorkerOptions _options;
    private readonly ILogger<InvestigationWorkerService> _logger;
    private readonly string _workerId;

    public InvestigationWorkerService(IInvestigationQueue queue, InvestigationJobRunner runner, IOptions<WorkerOptions> options, ILogger<InvestigationWorkerService> logger)
    {
        _queue = queue;
        _runner = runner;
        _options = options.Value;
        _logger = logger;
        _workerId = string.IsNullOrWhiteSpace(_options.WorkerId) ? $"{Environment.MachineName}-{Environment.ProcessId}" : _options.WorkerId!;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var slots = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentJobs));
        var running = new List<Task>();
        _logger.LogInformation("Worker {WorkerId} started with {Slots} slot(s).", _workerId, _options.MaxConcurrentJobs);

        while (!stoppingToken.IsCancellationRequested)
        {
            await slots.WaitAsync(stoppingToken);
            Guid? jobId;
            try
            {
                jobId = await _queue.DequeueAsync(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                slots.Release();
                _logger.LogWarning("Queue receive failed: {Message}", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            if (jobId is null)
            {
                slots.Release();
                continue;
            }

            running.RemoveAll(t => t.IsCompleted);
            running.Add(Task.Run(async () =>
            {
                try { await _runner.RunAsync(jobId.Value, _workerId, stoppingToken); }
                catch (Exception ex) { _logger.LogError("Job {JobId} runner error: {Message}", jobId, ex.Message); }
                finally { slots.Release(); }
            }, CancellationToken.None));
        }

        await Task.WhenAll(running);
    }
}

/// <summary>
/// Durability sweeper: re-queues jobs whose worker died (expired lease), purges expired secrets,
/// and publishes reports that could not be published yet (e.g. waiting for a fresh Jira app token).
/// </summary>
public sealed class JobRecoveryService : BackgroundService
{
    private readonly IInvestigationJobStore _jobs;
    private readonly IInvestigationQueue _queue;
    private readonly IJobSecretStore _secrets;
    private readonly JiraReportPublisher _publisher;
    private readonly ISecretRedactor _redactor;
    private readonly ILogger<JobRecoveryService> _logger;

    public JobRecoveryService(IInvestigationJobStore jobs, IInvestigationQueue queue, IJobSecretStore secrets,
        JiraReportPublisher publisher, ISecretRedactor redactor, ILogger<JobRecoveryService> logger)
    {
        _jobs = jobs;
        _queue = queue;
        _secrets = secrets;
        _publisher = publisher;
        _redactor = redactor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Recovery sweep failed: {Message}", _redactor.Redact(ex.Message));
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    public async Task SweepOnceAsync(CancellationToken ct)
    {
        foreach (var id in await _jobs.ListOrphanedAsync(20, ct))
        {
            var job = await _jobs.UpdateAsync(id, j =>
            {
                j.LeaseOwner = null;
                j.LeaseExpiresUnixMs = 0;
                if (j.RetryCount < j.MaxRetries)
                {
                    j.RetryCount++;
                    j.Status = InvestigationJobStatus.Queued;
                    j.StatusMessage = "The worker stopped unexpectedly — investigation re-queued.";
                }
                else
                {
                    j.Status = InvestigationJobStatus.Failed;
                    j.CompletedAt = DateTimeOffset.UtcNow;
                    j.StatusMessage = j.ErrorDetails = "The worker stopped unexpectedly too many times.";
                }
            }, ct);
            if (job?.Status == InvestigationJobStatus.Queued)
                await _queue.EnqueueAsync(id, null, ct);
        }

        await _secrets.PurgeExpiredAsync(ct);

        foreach (var id in await _jobs.ListUnpublishedCompletedAsync(10, ct))
        {
            if (await _publisher.PublishAsync(id, ct))
                await _secrets.DeleteAllAsync(id, null, ct);
        }
    }
}
