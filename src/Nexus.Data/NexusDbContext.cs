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
    public DbSet<EntraUserRecord> EntraUsers => Set<EntraUserRecord>();
    public DbSet<XdrEndpointRecord> XdrEndpoints => Set<XdrEndpointRecord>();
    public DbSet<NetskopeClientRecord> NetskopeClients => Set<NetskopeClientRecord>();
    public DbSet<IntunePolicyRecord> IntunePolicies => Set<IntunePolicyRecord>();
    public DbSet<IntuneDevicePolicyState> IntuneDevicePolicyStates => Set<IntuneDevicePolicyState>();
    public DbSet<MamRegistrationRecord> MamRegistrations => Set<MamRegistrationRecord>();
    public DbSet<InstalledSoftwareRecord> InstalledSoftware => Set<InstalledSoftwareRecord>();
    public DbSet<InventoryFetch> InventoryFetches => Set<InventoryFetch>();
    public DbSet<JobRun> JobRuns => Set<JobRun>();
    public DbSet<RawRecordVersion> RawRecordVersions => Set<RawRecordVersion>();
    public DbSet<EvidenceTimelineEntry> EvidenceTimeline => Set<EvidenceTimelineEntry>();
    public DbSet<AssetChange> AssetChanges => Set<AssetChange>();

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
            e.Property(x => x.ClientVersion).HasMaxLength(32);
            e.Property(x => x.LastLogonUser).HasMaxLength(256);
            e.Property(x => x.AdSite).HasMaxLength(128);
            e.Property(x => x.OsVersion).HasMaxLength(64);
            e.Property(x => x.CpuName).HasMaxLength(256);
            e.Property(x => x.BiosVersion).HasMaxLength(128);
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
            e.Property(x => x.UserId).HasMaxLength(64);
            e.Property(x => x.JailBroken).HasMaxLength(16);
            e.Property(x => x.DeviceRegistrationState).HasMaxLength(64);
            e.HasIndex(x => x.AzureAdDeviceId);
            e.HasIndex(x => x.UserId);
        });

        model.Entity<XdrEndpointRecord>(e =>
        {
            e.ToTable("xdr_endpoints");
            e.HasKey(x => x.AgentId);
            e.Property(x => x.AgentId).HasMaxLength(128).ValueGeneratedNever();
            e.Property(x => x.HostName).HasMaxLength(256);
            e.Property(x => x.AgentStatus).HasMaxLength(64);
            e.Property(x => x.OperationalStatus).HasMaxLength(64);
            e.Property(x => x.AgentType).HasMaxLength(64);
            e.Property(x => x.Ip).HasMaxLength(256);
            e.Property(x => x.Users).HasMaxLength(512);
            e.HasIndex(x => x.HostName);
        });

        model.Entity<NetskopeClientRecord>(e =>
        {
            e.ToTable("netskope_clients");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(128).ValueGeneratedNever();
            e.Property(x => x.DeviceId).HasMaxLength(128);
            e.Property(x => x.HostName).HasMaxLength(256);
            e.Property(x => x.OperatingSystem).HasMaxLength(128);
            e.Property(x => x.OsVersion).HasMaxLength(64);
            e.Property(x => x.Serial).HasMaxLength(128);
            e.Property(x => x.Manufacturer).HasMaxLength(128);
            e.Property(x => x.Model).HasMaxLength(128);
            e.Property(x => x.ClientVersion).HasMaxLength(64);
            e.Property(x => x.Status).HasMaxLength(64);
            e.Property(x => x.ManagementId).HasMaxLength(128);
            e.Property(x => x.Users).HasMaxLength(512);
            e.HasIndex(x => x.HostName);
        });

        model.Entity<EntraUserRecord>(e =>
        {
            e.ToTable("entra_users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64).ValueGeneratedNever();
            e.Property(x => x.UserPrincipalName).HasMaxLength(256);
            e.Property(x => x.DisplayName).HasMaxLength(256);
            e.Property(x => x.Department).HasMaxLength(256);
        });

        model.Entity<IntunePolicyRecord>(e =>
        {
            e.ToTable("intune_policies");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(96).ValueGeneratedNever();
            e.Property(x => x.Kind).HasMaxLength(24);
            e.Property(x => x.PolicyId).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(256);
            e.Property(x => x.Description).HasMaxLength(1024);
            e.Property(x => x.Platform).HasMaxLength(64);
            e.Property(x => x.Assignments).HasMaxLength(2048);
            e.HasIndex(x => x.Kind);
        });

        model.Entity<IntuneDevicePolicyState>(e =>
        {
            e.ToTable("intune_device_policy_states");
            e.Property(x => x.IntuneDeviceId).HasMaxLength(64);
            e.Property(x => x.Kind).HasMaxLength(24);
            e.Property(x => x.PolicyId).HasMaxLength(64);
            e.Property(x => x.PolicyName).HasMaxLength(256);
            e.Property(x => x.State).HasMaxLength(32);
            e.Property(x => x.Platform).HasMaxLength(64);
            e.HasIndex(x => x.IntuneDeviceId);
            e.HasIndex(x => new { x.Kind, x.PolicyName });
        });

        model.Entity<MamRegistrationRecord>(e =>
        {
            e.ToTable("mam_registrations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(128).ValueGeneratedNever();
            e.Property(x => x.UserId).HasMaxLength(64);
            e.Property(x => x.DeviceName).HasMaxLength(256);
            e.Property(x => x.DeviceTag).HasMaxLength(128);
            e.Property(x => x.DeviceType).HasMaxLength(32);
            e.Property(x => x.AppIdentifier).HasMaxLength(256);
            e.Property(x => x.AppVersion).HasMaxLength(64);
            e.Property(x => x.PlatformVersion).HasMaxLength(64);
            e.Property(x => x.FlaggedReasons).HasMaxLength(512);
            e.Property(x => x.AppliedPolicies).HasMaxLength(1024);
            e.Property(x => x.IntendedPolicies).HasMaxLength(1024);
            e.HasIndex(x => x.UserId);
        });

        model.Entity<InstalledSoftwareRecord>(e =>
        {
            e.ToTable("installed_software");
            e.Property(x => x.Source).HasMaxLength(16);
            e.Property(x => x.Name).HasMaxLength(512);
            e.Property(x => x.Version).HasMaxLength(128);
            e.Property(x => x.Publisher).HasMaxLength(256);
            e.HasIndex(x => x.AssetId);
        });

        model.Entity<JobRun>(e =>
        {
            e.ToTable("job_runs");
            e.Property(x => x.Job).HasMaxLength(64);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.Message).HasMaxLength(2000);
            e.HasIndex(x => new { x.Job, x.StartedAt });
            e.HasIndex(x => x.RunId);
        });

        model.Entity<RawRecordVersion>(e =>
        {
            e.ToTable("raw_record_versions");
            e.Property(x => x.Source).HasMaxLength(32);
            e.Property(x => x.SourceKey).HasMaxLength(256);
            e.Property(x => x.Hash).HasMaxLength(64);
            e.HasIndex(x => new { x.Source, x.SourceKey, x.IsCurrent });
            e.HasIndex(x => x.LastSeenAt);
        });

        model.Entity<EvidenceTimelineEntry>(e =>
        {
            e.ToTable("evidence_timeline");
            e.Property(x => x.Source).HasMaxLength(32);
            e.HasIndex(x => new { x.AssetId, x.Source, x.ObservedAt });
            e.HasIndex(x => x.CollectedAt);
        });

        model.Entity<AssetChange>(e =>
        {
            e.ToTable("asset_changes");
            e.Property(x => x.AssetName).HasMaxLength(256);
            e.Property(x => x.Field).HasMaxLength(64);
            e.Property(x => x.OldValue).HasMaxLength(512);
            e.Property(x => x.NewValue).HasMaxLength(512);
            e.HasIndex(x => new { x.AssetId, x.At });
            e.HasIndex(x => x.At);
        });

        model.Entity<InventoryFetch>(e =>
        {
            e.ToTable("inventory_fetches");
            e.HasKey(x => x.AssetId);
            e.Property(x => x.AssetId).ValueGeneratedNever();
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Message).HasMaxLength(1024);
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
            e.Property(x => x.IntuneUserId).HasMaxLength(64);
            e.Property(x => x.NetskopeStatus).HasMaxLength(64);
            e.Property(x => x.NetskopeVersion).HasMaxLength(64);
            e.Property(x => x.ActivityClass).HasMaxLength(16);
            e.Property(x => x.ActiveSources).HasMaxLength(128);
            e.Property(x => x.XdrStatus).HasMaxLength(64);
            e.Property(x => x.XdrOperationalStatus).HasMaxLength(64);
            e.Property(x => x.XdrAgentType).HasMaxLength(64);
            e.Property(x => x.XdrIp).HasMaxLength(256);
            e.Property(x => x.MamPolicies).HasMaxLength(1024);
            e.Property(x => x.SccmClientVersion).HasMaxLength(32);
            e.Property(x => x.CpuName).HasMaxLength(256);
            e.HasIndex(x => x.Coverage);
            e.HasIndex(x => x.Platform);
            e.HasIndex(x => x.ActivityClass);
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
