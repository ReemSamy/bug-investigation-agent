namespace BugInvestigationAgent.Core.Memory;

public enum MemoryScope
{
    Session = 0,
    Repository = 1,
    Project = 2,
    Team = 3,
    Organization = 4
}

public sealed class EngineeringLesson
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public MemoryScope Scope { get; set; } = MemoryScope.Repository;
    public string? ScopeKey { get; set; }
    public string Lesson { get; set; } = "";
    public IList<string> Tags { get; set; } = new List<string>();
    public int Version { get; set; } = 1;
    public string? ValidatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ValidatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsActive { get; set; } = true;
}

public interface IEngineeringMemoryStore
{
    Task<EngineeringLesson> SaveValidatedLessonAsync(EngineeringLesson lesson, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EngineeringLesson>> QueryRelevantAsync(string? scopeKey, IEnumerable<string>? tags, CancellationToken cancellationToken = default);
    Task<EngineeringLesson?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task DeactivateAsync(Guid id, CancellationToken cancellationToken = default);
}
