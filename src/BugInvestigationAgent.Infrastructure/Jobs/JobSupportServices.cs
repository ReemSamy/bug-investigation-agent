using System.Threading.Channels;
using Azure.Messaging.ServiceBus;
using BugInvestigationAgent.Core.Jobs;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace BugInvestigationAgent.Infrastructure.Jobs;

/// <summary>
/// Per-job secrets encrypted with Data Protection; purpose bound to (job, kind) so a value cannot be decrypted
/// under another job or kind. Expired values are never returned.
/// </summary>
public sealed class DataProtectionJobSecretStore : IJobSecretStore
{
    private readonly IDbContextFactory<AgentDbContext> _factory;
    private readonly IDataProtectionProvider _provider;

    public DataProtectionJobSecretStore(IDbContextFactory<AgentDbContext> factory, IDataProtectionProvider provider)
    {
        _factory = factory;
        _provider = provider;
    }

    private IDataProtector Protector(Guid jobId, string kind) =>
        _provider.CreateProtector("BugInvestigationAgent.JobSecrets.v1", jobId.ToString("N"), kind);

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public async Task SetAsync(Guid jobId, string kind, string value, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var row = await db.JobSecrets.FirstOrDefaultAsync(s => s.JobId == jobId && s.Kind == kind, cancellationToken);
        if (row is null)
        {
            row = new JobSecretEntity { JobId = jobId, Kind = kind };
            db.JobSecrets.Add(row);
        }

        row.ProtectedValue = Protector(jobId, kind).Protect(value);
        row.ExpiresUnixMs = expiresAt.ToUnixTimeMilliseconds();
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<string?> GetAsync(Guid jobId, string kind, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var now = Now;
        var row = await db.JobSecrets.AsNoTracking()
            .FirstOrDefaultAsync(s => s.JobId == jobId && s.Kind == kind && s.ExpiresUnixMs > now, cancellationToken);
        return row is null ? null : Unprotect(jobId, kind, row.ProtectedValue);
    }

    public async Task<string?> TakeAsync(Guid jobId, string kind, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var row = await db.JobSecrets.FirstOrDefaultAsync(s => s.JobId == jobId && s.Kind == kind, cancellationToken);
        if (row is null)
            return null;
        db.JobSecrets.Remove(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return null; // another consumer took it first — single use
        }

        return row.ExpiresUnixMs > Now ? Unprotect(jobId, kind, row.ProtectedValue) : null;
    }

    public async Task<bool> ExistsAsync(Guid jobId, string kind, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var now = Now;
        return await db.JobSecrets.AnyAsync(s => s.JobId == jobId && s.Kind == kind && s.ExpiresUnixMs > now, cancellationToken);
    }

    public async Task DeleteAllAsync(Guid jobId, IReadOnlyCollection<string>? keepKinds = null, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var keep = keepKinds?.ToList() ?? new List<string>();
        await db.JobSecrets.Where(s => s.JobId == jobId && !keep.Contains(s.Kind)).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var now = Now;
        await db.JobSecrets.Where(s => s.ExpiresUnixMs <= now).ExecuteDeleteAsync(cancellationToken);
    }

    private string? Unprotect(Guid jobId, string kind, string value)
    {
        try { return Protector(jobId, kind).Unprotect(value); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }
}

public sealed class EfAuditLog : IAuditLog
{
    private readonly IDbContextFactory<AgentDbContext> _factory;
    private readonly ISecretRedactor _redactor;

    public EfAuditLog(IDbContextFactory<AgentDbContext> factory, ISecretRedactor redactor)
    {
        _factory = factory;
        _redactor = redactor;
    }

    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        entry.Detail = entry.Detail is null ? null : _redactor.Redact(entry.Detail);
        db.AuditEntries.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuditEntry>> ListForJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.AuditEntries.AsNoTracking().Where(a => a.JobId == jobId).OrderBy(a => a.Id).ToListAsync(cancellationToken);
    }
}

/// <summary>
/// Queue backed by the jobs table: workers poll for Queued jobs whose back-off elapsed. A local wake-up channel
/// makes same-process enqueues immediate. Works with SQLite (dev) and PostgreSQL (hosted) — no extra infrastructure.
/// </summary>
public sealed class DatabaseInvestigationQueue : IInvestigationQueue
{
    private readonly IInvestigationJobStore _jobs;
    private readonly Channel<Guid> _local = Channel.CreateUnbounded<Guid>();

    public DatabaseInvestigationQueue(IInvestigationJobStore jobs) => _jobs = jobs;

    public async Task EnqueueAsync(Guid jobId, TimeSpan? delay = null, CancellationToken cancellationToken = default)
    {
        if (delay is { } d && d > TimeSpan.Zero)
        {
            var notBefore = DateTimeOffset.UtcNow.Add(d).ToUnixTimeMilliseconds();
            await _jobs.UpdateAsync(jobId, j => j.NotBeforeUnixMs = notBefore, cancellationToken);
            return;
        }

        _local.Writer.TryWrite(jobId);
    }

    public async Task<QueuedJobMessage?> DequeueAsync(TimeSpan wait, CancellationToken cancellationToken = default)
    {
        // Settlement is a no-op: the jobs table IS the queue (Queued status + lease), so nothing is lost on failure.
        if (_local.Reader.TryRead(out var local))
            return new QueuedJobMessage { JobId = local };

        var runnable = await _jobs.ListRunnableAsync(1, cancellationToken);
        if (runnable.Count > 0)
            return new QueuedJobMessage { JobId = runnable[0] };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(wait);
        try
        {
            return new QueuedJobMessage { JobId = await _local.Reader.ReadAsync(timeout.Token) };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}

/// <summary>Azure Service Bus queue (hosted). Messages carry only the job id; the DB lease guarantees single execution.</summary>
public sealed class ServiceBusInvestigationQueue : IInvestigationQueue, IAsyncDisposable
{
    private readonly ServiceBusSender _sender;
    private readonly ServiceBusReceiver _receiver;

    public ServiceBusInvestigationQueue(ServiceBusClient client, string queueName)
        : this(client.CreateSender(queueName),
               client.CreateReceiver(queueName, new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock }))
    {
    }

    public ServiceBusInvestigationQueue(ServiceBusSender sender, ServiceBusReceiver receiver)
    {
        _sender = sender;
        _receiver = receiver;
    }

    public async Task EnqueueAsync(Guid jobId, TimeSpan? delay = null, CancellationToken cancellationToken = default)
    {
        var message = new ServiceBusMessage(jobId.ToString("N")) { MessageId = $"{jobId:N}-{Guid.NewGuid():N}" };
        if (delay is { } d && d > TimeSpan.Zero)
            message.ScheduledEnqueueTime = DateTimeOffset.UtcNow.Add(d);
        await _sender.SendMessageAsync(message, cancellationToken);
    }

    public async Task<QueuedJobMessage?> DequeueAsync(TimeSpan wait, CancellationToken cancellationToken = default)
    {
        var message = await _receiver.ReceiveMessageAsync(wait, cancellationToken);
        if (message is null)
            return null;

        // Peek-lock: the worker completes the message once it holds the job lease (or when there is nothing to do),
        // abandons it on transient failures (Service Bus redelivers, then dead-letters after MaxDeliveryCount),
        // and dead-letters poison messages explicitly.
        return new QueuedJobMessage
        {
            JobId = Guid.TryParse(message.Body.ToString(), out var id) ? id : null,
            DeliveryCount = message.DeliveryCount,
            CompleteAsync = () => _receiver.CompleteMessageAsync(message, CancellationToken.None),
            AbandonAsync = () => _receiver.AbandonMessageAsync(message, null, CancellationToken.None),
            DeadLetterAsync = reason => _receiver.DeadLetterMessageAsync(message, "invalid-message", reason, CancellationToken.None)
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _sender.DisposeAsync();
        await _receiver.DisposeAsync();
    }
}
