using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ad_computers",
                columns: table => new
                {
                    ObjectGuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DnsHostName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    OperatingSystem = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    OperatingSystemVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LastLogonTimestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PasswordLastSet = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    WhenCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    DistinguishedName = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    CollectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ad_computers", x => x.ObjectGuid);
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "health_results",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ErrorCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Impact = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    HowToFix = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Script = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    CheckedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExecutedAs = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_health_results", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "job_states",
                columns: table => new
                {
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LastStartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastCompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    LastDurationMs = table.Column<int>(type: "int", nullable: true),
                    LastRecordCount = table.Column<int>(type: "int", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NextRunAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_states", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "sccm_devices",
                columns: table => new
                {
                    ResourceId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Domain = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Client = table.Column<bool>(type: "bit", nullable: true),
                    Active = table.Column<bool>(type: "bit", nullable: true),
                    Obsolete = table.Column<bool>(type: "bit", nullable: true),
                    AadDeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SmbiosGuid = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    OperatingSystem = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CollectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sccm_devices", x => x.ResourceId);
                });

            migrationBuilder.CreateTable(
                name: "setup_state",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    SetupModeActive = table.Column<bool>(type: "bit", nullable: false),
                    CodeHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CodeExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CodeUsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_setup_state", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "worker_commands",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Type = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Argument = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RequestedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Result = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_worker_commands", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_At",
                table: "audit_events",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_health_results_CheckedAt",
                table: "health_results",
                column: "CheckedAt");

            migrationBuilder.CreateIndex(
                name: "IX_health_results_RunId",
                table: "health_results",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_worker_commands_Status_Id",
                table: "worker_commands",
                columns: new[] { "Status", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ad_computers");

            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "health_results");

            migrationBuilder.DropTable(
                name: "job_states");

            migrationBuilder.DropTable(
                name: "sccm_devices");

            migrationBuilder.DropTable(
                name: "setup_state");

            migrationBuilder.DropTable(
                name: "worker_commands");
        }
    }
}
