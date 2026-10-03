using Microsoft.EntityFrameworkCore;
using Nexus.Data.Entities;

namespace Nexus.Data;

/// <summary>
/// Provider-neutral model. Each provider has its own subclass so that migrations of
/// SQL Server and PostgreSQL live side by side (see <see cref="SqlServerNexusDbContext"/>).
/// </summary>
public abstract class NexusDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<WorkerCommand> Commands => Set<WorkerCommand>();
    public DbSet<HealthResultRecord> HealthResults => Set<HealthResultRecord>();
    public DbSet<JobState> Jobs => Set<JobState>();
    public DbSet<SetupState> Setup => Set<SetupState>();
    public DbSet<SccmDeviceRecord> SccmDevices => Set<SccmDeviceRecord>();
    public DbSet<AdComputerRecord> AdComputers => Set<AdComputerRecord>();
    public DbSet<IntuneDeviceRecord> IntuneDevices => Set<IntuneDeviceRecord>();
    public DbSet<EntraDeviceRecord> EntraDevices => Set<EntraDeviceRecord>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<AssetLink> AssetLinks => Set<AssetLink>();
    public DbSet<ReviewItem> ReviewItems => Set<ReviewItem>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_events");
            e.Property(x => x.Actor).HasMaxLength(256);
            e.Property(x => x.Action).HasMaxLength(128);
            e.HasIndex(x => x.At);
        });

        model.Entity<WorkerCommand>(e =>
        {
            e.ToTable("worker_commands");
            e.Property(x => x.Type).HasMaxLength(64);
            e.Property(x => x.Argument).HasMaxLength(256);
            e.Property(x => x.RequestedBy).HasMaxLength(256);
            e.Property(x => x.Status).HasConversion<int>();
            e.HasIndex(x => new { x.Status, x.Id });
        });

        model.Entity<HealthResultRecord>(e =>
        {
            e.ToTable("health_results");
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.ErrorCode).HasMaxLength(64);
            e.Property(x => x.ExecutedAs).HasMaxLength(256);
            e.HasIndex(x => x.RunId);
            e.HasIndex(x => x.CheckedAt);
        });

        model.Entity<JobState>(e =>
        {
            e.ToTable("job_states");
            e.HasKey(x => x.Name);
            e.Property(x => x.Name).HasMaxLength(64);
            e.Property(x => x.LastStatus).HasMaxLength(32);
        });

        model.Entity<SetupState>(e =>
        {
            e.ToTable("setup_state");
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.CodeHash).HasMaxLength(64);
        });

        model.Entity<SccmDeviceRecord>(e =>
        {
            e.ToTable("sccm_devices");
            e.HasKey(x => x.ResourceId);
            e.Property(x => x.ResourceId).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(256);
            e.Property(x => x.Domain).HasMaxLength(256);
            e.Property(x => x.SmbiosGuid).HasMaxLength(64);
            e.Property(x => x.OperatingSystem).HasMaxLength(256);
            e.Property(x => x.Serial).HasMaxLength(128);
            e.Property(x => x.Manufacturer).HasMaxLength(128);
            e.Property(x => x.Model).HasMaxLength(128);
        });

        model.Entity<IntuneDeviceRecord>(e =>
        {
            e.ToTable("intune_devices");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64).ValueGeneratedNever();
            e.Property(x => x.DeviceName).HasMaxLength(256);
            e.Property(x => x.SerialNumber).HasMaxLength(128);
            e.Property(x => x.Manufacturer).HasMaxLength(128);
            e.Property(x => x.Model).HasMaxLength(128);
            e.Property(x => x.OperatingSystem).HasMaxLength(64);
            e.Property(x => x.OsVersion).HasMaxLength(64);
            e.Property(x => x.ManagementAgent).HasMaxLength(64);
            e.Property(x => x.EnrollmentType).HasMaxLength(64);
            e.Property(x => x.OwnerType).HasMaxLength(32);
            e.Property(x => x.ComplianceState).HasMaxLength(32);
            e.Property(x => x.UserPrincipalName).HasMaxLength(256);
            e.HasIndex(x => x.AzureAdDeviceId);
        });

        model.Entity<EntraDeviceRecord>(e =>
        {
            e.ToTable("entra_devices");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64).ValueGeneratedNever();
            e.Property(x => x.DisplayName).HasMaxLength(256);
            e.Property(x => x.TrustType).HasMaxLength(32);
            e.Property(x => x.OperatingSystem).HasMaxLength(64);
            e.Property(x => x.OperatingSystemVersion).HasMaxLength(64);
            e.Property(x => x.Ownership).HasMaxLength(32);
            e.HasIndex(x => x.DeviceId);
        });

        model.Entity<Asset>(e =>
        {
            e.ToTable("assets");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(256);
            e.Property(x => x.Serial).HasMaxLength(128);
            e.Property(x => x.Manufacturer).HasMaxLength(128);
            e.Property(x => x.Model).HasMaxLength(128);
            e.Property(x => x.Platform).HasMaxLength(32);
            e.Property(x => x.Ownership).HasMaxLength(16);
            e.Property(x => x.OwnershipSource).HasMaxLength(64);
            e.Property(x => x.Department).HasMaxLength(256);
            e.Property(x => x.PrimaryUser).HasMaxLength(256);
            e.Property(x => x.SccmHealth).HasMaxLength(16);
            e.Property(x => x.IntuneChannel).HasMaxLength(24);
            e.Property(x => x.ComplianceState).HasMaxLength(32);
            e.Property(x => x.EntraTrustType).HasMaxLength(32);
            e.Property(x => x.Coverage).HasMaxLength(16);
            e.Property(x => x.Confidence).HasMaxLength(8);
            e.HasIndex(x => x.Coverage);
            e.HasIndex(x => x.Platform);
            e.HasIndex(x => x.Name);
        });

        model.Entity<AssetLink>(e =>
        {
            e.ToTable("asset_links");
            e.Property(x => x.Source).HasMaxLength(16);
            e.Property(x => x.SourceKey).HasMaxLength(128);
            e.Property(x => x.Evidence).HasMaxLength(24);
            e.Property(x => x.Confidence).HasMaxLength(8);
            e.Property(x => x.Reason).HasMaxLength(512);
            e.HasIndex(x => x.AssetId);
            e.HasIndex(x => new { x.Source, x.SourceKey });
        });

        model.Entity<ReviewItem>(e =>
        {
            e.ToTable("review_items");
            e.Property(x => x.Kind).HasMaxLength(32);
            e.Property(x => x.Detail).HasMaxLength(1024);
        });

        model.Entity<AdComputerRecord>(e =>
        {
            e.ToTable("ad_computers");
            e.HasKey(x => x.ObjectGuid);
            e.Property(x => x.Name).HasMaxLength(256);
            e.Property(x => x.DnsHostName).HasMaxLength(256);
            e.Property(x => x.OperatingSystem).HasMaxLength(256);
            e.Property(x => x.OperatingSystemVersion).HasMaxLength(64);
            e.Property(x => x.DistinguishedName).HasMaxLength(1024);
        });
    }
}

public sealed class SqlServerNexusDbContext(DbContextOptions<SqlServerNexusDbContext> options) : NexusDbContext(options);

public sealed class PostgresNexusDbContext(DbContextOptions<PostgresNexusDbContext> options) : NexusDbContext(options);
