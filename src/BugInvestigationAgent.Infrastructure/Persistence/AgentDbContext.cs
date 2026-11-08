using BugInvestigationAgent.Core.Credentials;
using BugInvestigationAgent.Core.Memory;
using Microsoft.EntityFrameworkCore;

namespace BugInvestigationAgent.Infrastructure.Persistence;

public sealed class AgentDbContext : DbContext
{
    public AgentDbContext(DbContextOptions<AgentDbContext> options) : base(options)
    {
    }

    public DbSet<CredentialProfileEntity> CredentialProfiles => Set<CredentialProfileEntity>();
    public DbSet<EngineeringLessonEntity> EngineeringLessons => Set<EngineeringLessonEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CredentialProfileEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Label).HasMaxLength(256).IsRequired();
            e.Property(x => x.EncryptedSecret).IsRequired();
            e.HasIndex(x => new { x.SystemName, x.EnvironmentName, x.Username });
            e.HasIndex(x => new { x.OwnerId, x.EnvironmentId });
        });

        modelBuilder.Entity<EngineeringLessonEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Lesson).IsRequired();
            e.Property(x => x.TagsCsv).HasMaxLength(1024);
            e.HasIndex(x => x.ScopeKey);
        });
    }
}

public sealed class CredentialProfileEntity
{
    public Guid Id { get; set; }
    public string Label { get; set; } = "";
    public string? SystemName { get; set; }
    public string? EnvironmentName { get; set; }
    /// <summary>Registry environment ID (upper-case). Null only for legacy, unscoped rows.</summary>
    public string? EnvironmentId { get; set; }
    /// <summary>Owning user (ICurrentUserAccessor.UserId). Null only for legacy rows.</summary>
    public string? OwnerId { get; set; }
    public string? Username { get; set; }
    public string? Role { get; set; }
    public string AuthType { get; set; } = "password";
    public string EncryptedSecret { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class EngineeringLessonEntity
{
    public Guid Id { get; set; }
    public MemoryScope Scope { get; set; }
    public string? ScopeKey { get; set; }
    public string Lesson { get; set; } = "";
    public string TagsCsv { get; set; } = "";
    public int Version { get; set; }
    public string? ValidatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ValidatedAt { get; set; }
    public bool IsActive { get; set; }
}
