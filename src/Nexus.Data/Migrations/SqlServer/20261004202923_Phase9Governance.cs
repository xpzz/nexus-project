using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class Phase9Governance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastOperation",
                table: "mam_registrations",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Chassis",
                table: "assets",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastM365AccessAt",
                table: "assets",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "M365Workloads",
                table: "assets",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "access_evidence",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    UserPrincipalName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    EntraDeviceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    DeviceName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    OperatingSystem = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Browser = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    IsManaged = table.Column<bool>(type: "bit", nullable: true),
                    IsCompliant = table.Column<bool>(type: "bit", nullable: true),
                    TrustType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LastAccessAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Workloads = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Count = table.Column<int>(type: "int", nullable: false),
                    ClientApp = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CollectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_access_evidence", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "app_configs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Platform = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    LastModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Assignments = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    AppsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SettingsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CollectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_configs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "app_protection_policies",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Platform = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    LastModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: true),
                    IsAssigned = table.Column<bool>(type: "bit", nullable: false),
                    AssignedToAll = table.Column<bool>(type: "bit", nullable: false),
                    Assignments = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    AppsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SettingsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CollectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_protection_policies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "conditional_access_policies",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    State = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Users = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Applications = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Platforms = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    GrantControls = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RequiresCompliantDevice = table.Column<bool>(type: "bit", nullable: false),
                    RequiresApprovedApp = table.Column<bool>(type: "bit", nullable: false),
                    RequiresAppProtection = table.Column<bool>(type: "bit", nullable: false),
                    RequiresMfa = table.Column<bool>(type: "bit", nullable: false),
                    TargetsMicrosoft365 = table.Column<bool>(type: "bit", nullable: false),
                    CollectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conditional_access_policies", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_access_evidence_EntraDeviceId",
                table: "access_evidence",
                column: "EntraDeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_access_evidence_UserId",
                table: "access_evidence",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "access_evidence");

            migrationBuilder.DropTable(
                name: "app_configs");

            migrationBuilder.DropTable(
                name: "app_protection_policies");

            migrationBuilder.DropTable(
                name: "conditional_access_policies");

            migrationBuilder.DropColumn(
                name: "LastOperation",
                table: "mam_registrations");

            migrationBuilder.DropColumn(
                name: "Chassis",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "LastM365AccessAt",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "M365Workloads",
                table: "assets");
        }
    }
}
