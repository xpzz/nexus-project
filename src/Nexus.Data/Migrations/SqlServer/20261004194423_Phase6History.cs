using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class Phase6History : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "asset_changes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssetName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Field = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OldValue = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    NewValue = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_changes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "evidence_timeline",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CollectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evidence_timeline", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "job_runs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Job = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Records = table.Column<int>(type: "int", nullable: true),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "raw_record_versions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Source = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SourceKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsCurrent = table.Column<bool>(type: "bit", nullable: false),
                    RemovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_raw_record_versions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_asset_changes_AssetId_At",
                table: "asset_changes",
                columns: new[] { "AssetId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_changes_At",
                table: "asset_changes",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_evidence_timeline_AssetId_Source_ObservedAt",
                table: "evidence_timeline",
                columns: new[] { "AssetId", "Source", "ObservedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_evidence_timeline_CollectedAt",
                table: "evidence_timeline",
                column: "CollectedAt");

            migrationBuilder.CreateIndex(
                name: "IX_job_runs_Job_StartedAt",
                table: "job_runs",
                columns: new[] { "Job", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_job_runs_RunId",
                table: "job_runs",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_raw_record_versions_LastSeenAt",
                table: "raw_record_versions",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_raw_record_versions_Source_SourceKey_IsCurrent",
                table: "raw_record_versions",
                columns: new[] { "Source", "SourceKey", "IsCurrent" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "asset_changes");

            migrationBuilder.DropTable(
                name: "evidence_timeline");

            migrationBuilder.DropTable(
                name: "job_runs");

            migrationBuilder.DropTable(
                name: "raw_record_versions");
        }
    }
}
