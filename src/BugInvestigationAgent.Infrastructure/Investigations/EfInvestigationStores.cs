using BugInvestigationAgent.Core.Investigations;
using BugInvestigationAgent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BugInvestigationAgent.Infrastructure.Investigations;

public sealed class EfInvestigationRecordStore : IInvestigationRecordStore
{
    private readonly IDbContextFactory<AgentDbContext> _factory;

    public EfInvestigationRecordStore(IDbContextFactory<AgentDbContext> factory) => _factory = factory;

    public async Task AddAsync(InvestigationRecord record, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        record.CreatedAt = record.UpdatedAt = DateTimeOffset.UtcNow;
        db.InvestigationRecords.Add(record);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<InvestigationRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.InvestigationRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    }

    public async Task<InvestigationRecord?> FindByInvestigationAsync(Guid investigationId, DateTimeOffset notBefore, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        // DateTimeOffset comparisons are not translatable on SQLite: filter the (few) rows for this id in memory.
        var rows = await db.InvestigationRecords.AsNoTracking().Where(r => r.InvestigationId == investigationId).ToListAsync(cancellationToken);
        return rows.Where(r => r.CreatedAt >= notBefore).OrderByDescending(r => r.CreatedAt).FirstOrDefault();
    }

    public async Task<IReadOnlyList<InvestigationRecord>> ListAsync(Guid? batchId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var query = db.InvestigationRecords.AsNoTracking();
        if (batchId is Guid b)
            query = query.Where(r => r.BatchId == b);
        var rows = await query.ToListAsync(cancellationToken);
        return rows.OrderByDescending(r => r.CreatedAt).ToList();
    }

    public async Task<bool> TryUpdateAsync(InvestigationRecord record, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var expected = record.Version;
        record.Version = expected + 1;
        record.UpdatedAt = DateTimeOffset.UtcNow;
        var entry = db.InvestigationRecords.Attach(record);
        entry.State = EntityState.Modified;
        entry.Property(r => r.Version).OriginalValue = expected;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            record.Version = expected;
            return false;
        }
    }
}

public sealed class EfLegacyBatchStore : ILegacyBatchStore
{
    private readonly IDbContextFactory<AgentDbContext> _factory;

    public EfLegacyBatchStore(IDbContextFactory<AgentDbContext> factory) => _factory = factory;

    public async Task AddAsync(LegacyBatch batch, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        db.LegacyBatches.Add(batch);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<LegacyBatch?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var batch = await db.LegacyBatches.AsNoTracking().Include(b => b.Items).FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
        batch?.Items.Sort((a, b) => a.Position.CompareTo(b.Position));
        return batch;
    }

    public async Task<IReadOnlyList<LegacyBatch>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.LegacyBatches.AsNoTracking().Include(b => b.Items).ToListAsync(cancellationToken);
        return rows.OrderByDescending(b => b.CreatedAt).ToList();
    }

    public async Task UpdateItemAsync(LegacyBatchItem item, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        item.UpdatedAt = DateTimeOffset.UtcNow;
        db.LegacyBatchItems.Update(item);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateBatchAsync(LegacyBatch batch, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var row = await db.LegacyBatches.FirstAsync(b => b.Id == batch.Id, cancellationToken);
        row.LastMembershipCheckAt = batch.LastMembershipCheckAt;
        row.LastMembershipChange = batch.LastMembershipChange;
        await db.SaveChangesAsync(cancellationToken);
    }
}
