using BugInvestigationAgent.Core.Jobs;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BugInvestigationAgent.Infrastructure.Jobs;

/// <summary>
/// Durable job store. Each call uses its own DbContext (thread-safe for concurrent workers/requests).
/// Worker exclusivity uses an atomic conditional UPDATE (lease), not in-memory locks.
/// </summary>
public sealed class EfInvestigationJobStore : IInvestigationJobStore
{
    private readonly IDbContextFactory<AgentDbContext> _factory;
    private readonly ISecretRedactor _redactor;

    public EfInvestigationJobStore(IDbContextFactory<AgentDbContext> factory, ISecretRedactor redactor)
    {
        _factory = factory;
        _redactor = redactor;
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public async Task<InvestigationJob> CreateAsync(InvestigationJob job, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        job.CreatedAt = job.UpdatedAt = DateTimeOffset.UtcNow;
        job.CreatedAtUnixMs = job.CreatedAt.ToUnixTimeMilliseconds();
        db.InvestigationJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return job;
    }

    public async Task<InvestigationJob?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.InvestigationJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
    }

    public async Task<InvestigationJob?> FindByContextAsync(string tenantId, string contextId, string accountId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.InvestigationJobs.AsNoTracking()
            .Where(j => j.TenantId == tenantId && j.ContextId == contextId && j.InvokingAccountId == accountId)
            .ToListAsync(cancellationToken);
        return rows.OrderByDescending(j => j.CreatedAtUnixMs).FirstOrDefault();
    }

    public async Task<IReadOnlyList<InvestigationJob>> ListAwaitingInputForAccountAsync(string tenantId, string accountId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.InvestigationJobs.AsNoTracking()
            .Where(j => j.TenantId == tenantId && j.InvokingAccountId == accountId && j.Status == InvestigationJobStatus.AwaitingUserInput)
            .ToListAsync(cancellationToken);
        return rows.OrderByDescending(j => j.CreatedAtUnixMs).ToList();
    }

    public async Task<InvestigationJob?> UpdateAsync(Guid id, Action<InvestigationJob> mutate, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var job = await db.InvestigationJobs.FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
            if (job is null)
                return null;
            mutate(job);
            job.UpdatedAt = DateTimeOffset.UtcNow;
            job.Version++;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return job;
            }
            catch (DbUpdateConcurrencyException)
            {
                await Task.Delay(20 * (attempt + 1), cancellationToken);
            }
        }

        throw new InvalidOperationException($"Could not update job {id} after repeated concurrent modifications.");
    }

    public async Task AppendEventAsync(Guid jobId, string kind, string message, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var text = _redactor.Redact(message);
        db.InvestigationJobEvents.Add(new InvestigationJobEvent
        {
            JobId = jobId,
            Kind = kind,
            Message = text.Length > 2000 ? text[..2000] + "…" : text
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<InvestigationJobEvent>> ListEventsAsync(Guid jobId, int max = 50, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.InvestigationJobEvents.AsNoTracking()
            .Where(e => e.JobId == jobId)
            .OrderByDescending(e => e.Id)
            .Take(max)
            .ToListAsync(cancellationToken);
        rows.Reverse();
        return rows;
    }

    public async Task<bool> TryAcquireLeaseAsync(Guid jobId, string owner, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var now = Now;
        var until = now + (long)duration.TotalMilliseconds;
        var rows = await db.InvestigationJobs
            .Where(j => j.Id == jobId
                        && (j.LeaseOwner == null || j.LeaseExpiresUnixMs < now || j.LeaseOwner == owner)
                        && j.Status != InvestigationJobStatus.Completed
                        && j.Status != InvestigationJobStatus.Failed
                        && j.Status != InvestigationJobStatus.Cancelled
                        && j.Status != InvestigationJobStatus.TimedOut)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.LeaseOwner, owner)
                .SetProperty(j => j.LeaseExpiresUnixMs, until)
                .SetProperty(j => j.Version, j => j.Version + 1), cancellationToken);
        return rows == 1;
    }

    public async Task<bool> RenewLeaseAsync(Guid jobId, string owner, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var until = Now + (long)duration.TotalMilliseconds;
        var rows = await db.InvestigationJobs
            .Where(j => j.Id == jobId && j.LeaseOwner == owner)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.LeaseExpiresUnixMs, until)
                .SetProperty(j => j.Version, j => j.Version + 1), cancellationToken);
        return rows == 1;
    }

    public async Task ReleaseLeaseAsync(Guid jobId, string owner, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await db.InvestigationJobs
            .Where(j => j.Id == jobId && j.LeaseOwner == owner)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.LeaseOwner, (string?)null)
                .SetProperty(j => j.LeaseExpiresUnixMs, 0L)
                .SetProperty(j => j.Version, j => j.Version + 1), cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> ListRunnableAsync(int max, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var now = Now;
        return await db.InvestigationJobs.AsNoTracking()
            .Where(j => j.Status == InvestigationJobStatus.Queued && j.NotBeforeUnixMs <= now
                        && (j.LeaseOwner == null || j.LeaseExpiresUnixMs < now))
            .OrderBy(j => j.CreatedAtUnixMs)
            .Select(j => j.Id)
            .Take(max)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> ListOrphanedAsync(int max, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var now = Now;
        return await db.InvestigationJobs.AsNoTracking()
            .Where(j => (j.Status == InvestigationJobStatus.Running || j.Status == InvestigationJobStatus.AwaitingUserInput)
                        && j.LeaseOwner != null && j.LeaseExpiresUnixMs < now)
            .Select(j => j.Id)
            .Take(max)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> ListUnpublishedCompletedAsync(int max, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.InvestigationJobs.AsNoTracking()
            .Where(j => j.ReportJson != null && j.ReportPublishedAt == null
                        && (j.Status == InvestigationJobStatus.Completed || j.Status == InvestigationJobStatus.Failed
                            || j.Status == InvestigationJobStatus.TimedOut))
            .Select(j => j.Id)
            .Take(max)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> ListExpiredAsync(long awaitingBeforeUnixMs, long createdBeforeUnixMs, int max, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var now = Now;
        return await db.InvestigationJobs.AsNoTracking()
            .Where(j => j.Status != InvestigationJobStatus.Completed && j.Status != InvestigationJobStatus.Failed
                        && j.Status != InvestigationJobStatus.Cancelled && j.Status != InvestigationJobStatus.TimedOut
                        && (j.LeaseOwner == null || j.LeaseExpiresUnixMs < now)
                        && ((j.Status == InvestigationJobStatus.AwaitingUserInput && j.AwaitingSinceUnixMs > 0 && j.AwaitingSinceUnixMs < awaitingBeforeUnixMs)
                            || j.CreatedAtUnixMs < createdBeforeUnixMs))
            .Select(j => j.Id)
            .Take(max)
            .ToListAsync(cancellationToken);
    }

    public async Task UpsertInstallationAsync(JiraInstallation installation, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.JiraInstallations.FirstOrDefaultAsync(i => i.InstallationId == installation.InstallationId, cancellationToken);
        if (existing is null)
        {
            db.JiraInstallations.Add(installation);
        }
        else
        {
            existing.CloudId = installation.CloudId;
            existing.SiteUrl = installation.SiteUrl ?? existing.SiteUrl;
            existing.ApiBaseUrl = installation.ApiBaseUrl ?? existing.ApiBaseUrl;
            existing.InstallerAccountId = installation.InstallerAccountId ?? existing.InstallerAccountId;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<JiraInstallation?> GetInstallationAsync(string installationId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.JiraInstallations.AsNoTracking().FirstOrDefaultAsync(i => i.InstallationId == installationId, cancellationToken);
    }
}
