using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class Phase1Inventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClientActiveStatus",
                table: "sccm_devices",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastActiveAt",
                table: "sccm_devices",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Manufacturer",
                table: "sccm_devices",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Model",
                table: "sccm_devices",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Serial",
                table: "sccm_devices",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "asset_links",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SourceKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Evidence = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    Confidence = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_links", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "assets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Serial = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Manufacturer = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Model = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Platform = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Ownership = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    OwnershipSource = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Department = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    PrimaryUser = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    InSccm = table.Column<bool>(type: "bit", nullable: false),
                    SccmHealth = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SccmClient = table.Column<bool>(type: "bit", nullable: false),
                    InIntune = table.Column<bool>(type: "bit", nullable: false),
                    IntuneChannel = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    ComplianceState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    InEntra = table.Column<bool>(type: "bit", nullable: false),
                    EntraTrustType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    InAd = table.Column<bool>(type: "bit", nullable: false),
                    AdEnabled = table.Column<bool>(type: "bit", nullable: false),
                    AdByNameOnly = table.Column<bool>(type: "bit", nullable: false),
                    SccmLastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IntuneLastSyncAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EntraLastSignInAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AdLastLogonAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastActivityAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Coverage = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Confidence = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    NeedsReview = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "entra_devices",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    TrustType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    LastSignInAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AccountEnabled = table.Column<bool>(type: "bit", nullable: true),
                    OperatingSystem = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    OperatingSystemVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Ownership = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    RegisteredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CollectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entra_devices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "intune_devices",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DeviceName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    AzureAdDeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SerialNumber = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Manufacturer = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Model = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    OperatingSystem = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    OsVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ManagementAgent = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    EnrollmentType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    OwnerType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    LastSyncAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EnrolledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ComplianceState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    UserPrincipalName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CollectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_intune_devices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "review_items",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    Sources = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_items", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_asset_links_AssetId",
                table: "asset_links",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_asset_links_Source_SourceKey",
                table: "asset_links",
                columns: new[] { "Source", "SourceKey" });

            migrationBuilder.CreateIndex(
                name: "IX_assets_Coverage",
                table: "assets",
                column: "Coverage");

            migrationBuilder.CreateIndex(
                name: "IX_assets_Name",
                table: "assets",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_assets_Platform",
                table: "assets",
                column: "Platform");

            migrationBuilder.CreateIndex(
                name: "IX_entra_devices_DeviceId",
                table: "entra_devices",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_intune_devices_AzureAdDeviceId",
                table: "intune_devices",
                column: "AzureAdDeviceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "asset_links");

            migrationBuilder.DropTable(
                name: "assets");

            migrationBuilder.DropTable(
                name: "entra_devices");

            migrationBuilder.DropTable(
                name: "intune_devices");

            migrationBuilder.DropTable(
                name: "review_items");

            migrationBuilder.DropColumn(
                name: "ClientActiveStatus",
                table: "sccm_devices");

            migrationBuilder.DropColumn(
                name: "LastActiveAt",
                table: "sccm_devices");

            migrationBuilder.DropColumn(
                name: "Manufacturer",
                table: "sccm_devices");

            migrationBuilder.DropColumn(
                name: "Model",
                table: "sccm_devices");

            migrationBuilder.DropColumn(
                name: "Serial",
                table: "sccm_devices");
        }
    }
}
