using BugInvestigationAgent.Core.Jobs;

namespace BugInvestigationAgent.Infrastructure.Hosting;

/// <summary>
/// Single place that turns user input (chat reply, securely submitted credentials or MFA code) into progress for a
/// SUSPENDED job: the job is atomically moved from Awaiting User Input (no worker) to Queued and enqueued once.
/// A job whose worker still holds it (e.g. a short MFA hold) is not re-queued — that worker picks the input up.
/// </summary>
public sealed class JobInputService
{
    private readonly IInvestigationJobStore _jobs;
    private readonly IInvestigationQueue _queue;
    private readonly IJobSecretStore _secrets;

    public JobInputService(IInvestigationJobStore jobs, IInvestigationQueue queue, IJobSecretStore secrets)
    {
        _jobs = jobs;
        _queue = queue;
        _secrets = secrets;
    }

    public async Task<InvestigationJob?> RecordReplyAsync(Guid jobId, string text, CancellationToken ct = default)
    {
        var job = await _jobs.UpdateAsync(jobId, j => j.PendingUserReply = text.Length > 500 ? text[..500] : text, ct);
        await WakeAsync(jobId, ct);
        return job is null ? null : await _jobs.GetAsync(jobId, ct);
    }

    /// <summary>Re-queues a suspended job exactly once (no-op if running, leased or terminal).</summary>
    public async Task<bool> WakeAsync(Guid jobId, CancellationToken ct = default)
    {
        var woke = false;
        await _jobs.UpdateAsync(jobId, j =>
        {
            woke = false;
            if (j.Status == InvestigationJobStatus.AwaitingUserInput && j.LeaseOwner is null && !j.CancelRequested)
            {
                j.Status = InvestigationJobStatus.Queued;
                j.StatusMessage = "Input received — resuming the investigation.";
                j.NotBeforeUnixMs = 0;
                woke = true;
            }
        }, ct);
        if (woke)
            await _queue.EnqueueAsync(jobId, null, ct);
        return woke;
    }

    /// <summary>
    /// Cancels one job. A job without a worker (queued or suspended) is cancelled immediately; a running job is
    /// flagged and its worker stops at the next check. Secrets of a cancelled job are deleted.
    /// </summary>
    public async Task<InvestigationJob?> CancelAsync(Guid jobId, CancellationToken ct = default)
    {
        var finishedNow = false;
        var job = await _jobs.UpdateAsync(jobId, j =>
        {
            if (j.IsTerminal)
                return;
            j.CancelRequested = true;
            j.StatusMessage = "Investigation cancelled.";
            if (j.LeaseOwner is null || j.LeaseExpiresUnixMs < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            {
                j.Status = InvestigationJobStatus.Cancelled;
                j.CompletedAt = DateTimeOffset.UtcNow;
                j.ContextSnapshotJson = null;
                finishedNow = true;
            }
        }, ct);
        if (finishedNow)
            await _secrets.DeleteAllAsync(jobId, null, ct);
        return job;
    }

    /// <summary>Called by a worker right after it suspended a job: input that arrived during suspension is not lost.</summary>
    public async Task<bool> WakeIfInputPendingAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await _jobs.GetAsync(jobId, ct);
        if (job is null || job.Status != InvestigationJobStatus.AwaitingUserInput)
            return false;
        var pending = !string.IsNullOrWhiteSpace(job.PendingUserReply)
                      || await _secrets.ExistsAsync(jobId, JobSecretKinds.MfaCode, ct)
                      || (job.EnvironmentId is { } env && await _secrets.ExistsAsync(jobId, JobSecretKinds.EnvironmentCredentials(env), ct));
        return pending && await WakeAsync(jobId, ct);
    }
}
