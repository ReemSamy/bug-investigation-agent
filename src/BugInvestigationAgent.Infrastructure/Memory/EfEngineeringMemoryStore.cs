using BugInvestigationAgent.Core.Memory;
using BugInvestigationAgent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BugInvestigationAgent.Infrastructure.Memory;

public sealed class EfEngineeringMemoryStore : IEngineeringMemoryStore
{
    private readonly AgentDbContext _db;

    public EfEngineeringMemoryStore(AgentDbContext db)
    {
        _db = db;
    }

    public async Task<EngineeringLesson> SaveValidatedLessonAsync(EngineeringLesson lesson, CancellationToken cancellationToken = default)
    {
        var entity = new EngineeringLessonEntity
        {
            Id = lesson.Id == Guid.Empty ? Guid.NewGuid() : lesson.Id,
            Scope = lesson.Scope,
            ScopeKey = lesson.ScopeKey,
            Lesson = lesson.Lesson,
            TagsCsv = string.Join(',', lesson.Tags.Where(t => !string.IsNullOrWhiteSpace(t))),
            Version = lesson.Version <= 0 ? 1 : lesson.Version,
            ValidatedBy = lesson.ValidatedBy,
            CreatedAt = DateTimeOffset.UtcNow,
            ValidatedAt = DateTimeOffset.UtcNow,
            IsActive = true
        };
        _db.EngineeringLessons.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<IReadOnlyList<EngineeringLesson>> QueryRelevantAsync(
        string? scopeKey,
        IEnumerable<string>? tags,
        CancellationToken cancellationToken = default)
    {
        var tagList = tags?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList() ?? new List<string>();
        var query = _db.EngineeringLessons.AsNoTracking().Where(x => x.IsActive);

        if (!string.IsNullOrWhiteSpace(scopeKey))
            query = query.Where(x => x.ScopeKey == null || x.ScopeKey == scopeKey || x.Scope == MemoryScope.Organization || x.Scope == MemoryScope.Team);

        // SQLite cannot ORDER BY DateTimeOffset in SQL — materialize then order on the client.
        var rows = (await query.ToListAsync(cancellationToken))
            .OrderByDescending(x => x.ValidatedAt)
            .Take(100)
            .ToList();
        if (tagList.Count == 0)
            return rows.Select(Map).Take(20).ToList();

        return rows
            .Where(r => tagList.Any(t => r.TagsCsv.Contains(t, StringComparison.OrdinalIgnoreCase) || r.Lesson.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .Select(Map)
            .Take(20)
            .ToList();
    }

    public async Task<EngineeringLesson?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await _db.EngineeringLessons.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        return row is null ? null : Map(row);
    }

    public async Task DeactivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await _db.EngineeringLessons.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (row is null)
            return;
        row.IsActive = false;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static EngineeringLesson Map(EngineeringLessonEntity e) => new()
    {
        Id = e.Id,
        Scope = e.Scope,
        ScopeKey = e.ScopeKey,
        Lesson = e.Lesson,
        Tags = string.IsNullOrWhiteSpace(e.TagsCsv)
            ? new List<string>()
            : e.TagsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        Version = e.Version,
        ValidatedBy = e.ValidatedBy,
        CreatedAt = e.CreatedAt,
        ValidatedAt = e.ValidatedAt,
        IsActive = e.IsActive
    };
}
