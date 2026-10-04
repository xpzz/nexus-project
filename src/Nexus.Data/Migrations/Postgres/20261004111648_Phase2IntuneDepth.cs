using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Nexus.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class Phase2IntuneDepth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AdSite",
                table: "sccm_devices",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BiosVersion",
                table: "sccm_devices",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientVersion",
                table: "sccm_devices",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CpuCores",
                table: "sccm_devices",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CpuName",
                table: "sccm_devices",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DiskFreeMb",
                table: "sccm_devices",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DiskTotalMb",
                table: "sccm_devices",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastBootAt",
                table: "sccm_devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastDdrAt",
                table: "sccm_devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastHwScanAt",
                table: "sccm_devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastLogonUser",
                table: "sccm_devices",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastPolicyRequestAt",
                table: "sccm_devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSwScanAt",
                table: "sccm_devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MemoryMb",
                table: "sccm_devices",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OsVersion",
                table: "sccm_devices",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AutopilotEnrolled",
                table: "intune_devices",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ComplianceGraceExpiresAt",
                table: "intune_devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeviceRegistrationState",
                table: "intune_devices",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "FreeStorageBytes",
                table: "intune_devices",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsEncrypted",
                table: "intune_devices",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSupervised",
                table: "intune_devices",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "JailBroken",
                table: "intune_devices",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PhysicalMemoryBytes",
                table: "intune_devices",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TotalStorageBytes",
                table: "intune_devices",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "intune_devices",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CompliancePolicies",
                table: "assets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CompliancePoliciesFailed",
                table: "assets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ConfigProfiles",
                table: "assets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ConfigProfilesFailed",
                table: "assets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CpuName",
                table: "assets",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DiskFreeMb",
                table: "assets",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DiskTotalMb",
                table: "assets",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasMam",
                table: "assets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "IntuneUserId",
                table: "assets",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsEncrypted",
                table: "assets",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "JailBroken",
                table: "assets",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MamAppCount",
                table: "assets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MamLastSyncAt",
                table: "assets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MamPolicies",
                table: "assets",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MemoryMb",
                table: "assets",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PoliciesCollected",
                table: "assets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SccmClientVersion",
                table: "assets",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SccmLastHwScanAt",
                table: "assets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SccmLastPolicyAt",
                table: "assets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UserEnabled",
                table: "assets",
                type: "boolean",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "entra_users",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserPrincipalName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Department = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AccountEnabled = table.Column<bool>(type: "boolean", nullable: true),
                    CollectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entra_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "installed_software",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Publisher = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    InstalledOn = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_installed_software", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "intune_device_policy_states",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IntuneDeviceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    PolicyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PolicyName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Platform = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SettingCount = table.Column<int>(type: "integer", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: true),
                    CollectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_intune_device_policy_states", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "intune_policies",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    PolicyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Platform = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: true),
                    LastModifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Assignments = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    AssignedToAll = table.Column<bool>(type: "boolean", nullable: false),
                    AssignmentCount = table.Column<int>(type: "integer", nullable: false),
                    CollectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_intune_policies", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "inventory_fetches",
                columns: table => new
                {
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FetchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Message = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_fetches", x => x.AssetId);
                });

            migrationBuilder.CreateTable(
                name: "mam_registrations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DeviceName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DeviceTag = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    DeviceType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    AppIdentifier = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AppVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PlatformVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastSyncAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FlaggedReasons = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    AppliedPolicies = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    IntendedPolicies = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CollectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mam_registrations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_intune_devices_UserId",
                table: "intune_devices",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_installed_software_AssetId",
                table: "installed_software",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_intune_device_policy_states_IntuneDeviceId",
                table: "intune_device_policy_states",
                column: "IntuneDeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_intune_device_policy_states_Kind_PolicyName",
                table: "intune_device_policy_states",
                columns: new[] { "Kind", "PolicyName" });

            migrationBuilder.CreateIndex(
                name: "IX_intune_policies_Kind",
                table: "intune_policies",
                column: "Kind");

            migrationBuilder.CreateIndex(
                name: "IX_mam_registrations_UserId",
                table: "mam_registrations",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "entra_users");

            migrationBuilder.DropTable(
                name: "installed_software");

            migrationBuilder.DropTable(
                name: "intune_device_policy_states");

            migrationBuilder.DropTable(
                name: "intune_policies");

            migrationBuilder.DropTable(
                name: "inventory_fetches");

            migrationBuilder.DropTable(
                name: "mam_registrations");

            migrationBuilder.DropIndex(
                name: "IX_intune_devices_UserId",
                table: "intune_devices");

            migrationBuilder.DropColumn(
                name: "AdSite",
                table: "sccm_devices");

            migrationBuilder.DropColumn(
                name: "BiosVersion",
                table: "sccm_devices");

            migrationBuilder.DropColumn(
                name: "ClientVersion",
                table: "sccm_devices");

            migrationBuilder.DropColumn(
                name: "CpuCores",
                table: "sccm_devices");

            migrationBuilder.DropColumn(
                name: "CpuName",
                table: "sccm_devices");

            migrationBuilder.DropColumn(
                name: "DiskFreeMb",
                table: "sccm_devices");

            migrationBuilder.DropColumn(
                name: "DiskTotalMb",
                table: "sccm_devices");

            migrationBuilder.DropColumn(
                name: "LastBootAt",
                table: "sccm_devices");

            migrationBuilder.DropColumn(
                name: "LastDdrAt",
                table: "sccm_devices");

            migrationBuilder.DropColumn(
                name: "LastHwScanAt",
                table: "sccm_devices");

            migrationBuilder.DropColumn(
                name: "LastLogonUser",
                table: "sccm_devices");

            migrationBuilder.DropColumn(
                name: "LastPolicyRequestAt",
                table: "sccm_devices");

            migrationBuilder.DropColumn(
                name: "LastSwScanAt",
                table: "sccm_devices");

            migrationBuilder.DropColumn(
                name: "MemoryMb",
                table: "sccm_devices");

            migrationBuilder.DropColumn(
                name: "OsVersion",
                table: "sccm_devices");

            migrationBuilder.DropColumn(
                name: "AutopilotEnrolled",
                table: "intune_devices");

            migrationBuilder.DropColumn(
                name: "ComplianceGraceExpiresAt",
                table: "intune_devices");

            migrationBuilder.DropColumn(
                name: "DeviceRegistrationState",
                table: "intune_devices");

            migrationBuilder.DropColumn(
                name: "FreeStorageBytes",
                table: "intune_devices");

            migrationBuilder.DropColumn(
                name: "IsEncrypted",
                table: "intune_devices");

            migrationBuilder.DropColumn(
                name: "IsSupervised",
                table: "intune_devices");

            migrationBuilder.DropColumn(
                name: "JailBroken",
                table: "intune_devices");

            migrationBuilder.DropColumn(
                name: "PhysicalMemoryBytes",
                table: "intune_devices");

            migrationBuilder.DropColumn(
                name: "TotalStorageBytes",
                table: "intune_devices");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "intune_devices");

            migrationBuilder.DropColumn(
                name: "CompliancePolicies",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "CompliancePoliciesFailed",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "ConfigProfiles",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "ConfigProfilesFailed",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "CpuName",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "DiskFreeMb",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "DiskTotalMb",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "HasMam",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "IntuneUserId",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "IsEncrypted",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "JailBroken",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "MamAppCount",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "MamLastSyncAt",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "MamPolicies",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "MemoryMb",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "PoliciesCollected",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "SccmClientVersion",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "SccmLastHwScanAt",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "SccmLastPolicyAt",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "UserEnabled",
                table: "assets");
        }
    }
}
