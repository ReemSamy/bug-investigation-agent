using BugInvestigationAgent.Core.Jobs;
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
    public DbSet<InvestigationJob> InvestigationJobs => Set<InvestigationJob>();
    public DbSet<InvestigationJobEvent> InvestigationJobEvents => Set<InvestigationJobEvent>();
    public DbSet<JobSecretEntity> JobSecrets => Set<JobSecretEntity>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<JiraInstallation> JiraInstallations => Set<JiraInstallation>();

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

        modelBuilder.Entity<InvestigationJob>(e =>
        {
            e.ToTable("InvestigationJobs");
            e.HasKey(x => x.Id);
            e.Ignore(x => x.IsTerminal);
            e.Property(x => x.TenantId).HasMaxLength(256).IsRequired();
            e.Property(x => x.CloudId).HasMaxLength(128).IsRequired();
            e.Property(x => x.IssueId).HasMaxLength(64).IsRequired();
            e.Property(x => x.IssueKey).HasMaxLength(64).IsRequired();
            e.Property(x => x.InvokingAccountId).HasMaxLength(128).IsRequired();
            e.Property(x => x.ContextId).HasMaxLength(128);
            e.Property(x => x.EnvironmentId).HasMaxLength(128);
            e.Property(x => x.LeaseOwner).HasMaxLength(128);
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.TenantId, x.ContextId });
            e.HasIndex(x => new { x.TenantId, x.InvokingAccountId });
            e.HasIndex(x => new { x.Status, x.NotBeforeUnixMs });
        });

        modelBuilder.Entity<InvestigationJobEvent>(e =>
        {
            e.ToTable("InvestigationJobEvents");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasMaxLength(64);
            e.HasIndex(x => x.JobId);
        });

        modelBuilder.Entity<JobSecretEntity>(e =>
        {
            e.ToTable("JobSecrets");
            e.HasKey(x => new { x.JobId, x.Kind });
            e.Property(x => x.Kind).HasMaxLength(160);
            e.Property(x => x.ProtectedValue).IsRequired();
            e.HasIndex(x => x.ExpiresUnixMs);
        });

        modelBuilder.Entity<AuditEntry>(e =>
        {
            e.ToTable("AuditEntries");
            e.HasKey(x => x.Id);
            e.Property(x => x.Action).HasMaxLength(64).IsRequired();
            e.Property(x => x.AccountId).HasMaxLength(128);
            e.Property(x => x.TenantId).HasMaxLength(256);
            e.HasIndex(x => x.JobId);
        });

        modelBuilder.Entity<JiraInstallation>(e =>
        {
            e.ToTable("JiraInstallations");
            e.HasKey(x => x.InstallationId);
            e.Property(x => x.InstallationId).HasMaxLength(256);
            e.Property(x => x.CloudId).HasMaxLength(128).IsRequired();
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

/// <summary>Encrypted (Data Protection) per-job secret. Never stored in plaintext.</summary>
public sealed class JobSecretEntity
{
    public Guid JobId { get; set; }
    public string Kind { get; set; } = "";
    public string ProtectedValue { get; set; } = "";
    public long ExpiresUnixMs { get; set; }
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
