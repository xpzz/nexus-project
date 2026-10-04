using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class Phase3Xdr : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "InXdr",
                table: "assets",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "XdrAgentType",
                table: "assets",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "XdrByNameOnly",
                table: "assets",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "XdrIp",
                table: "assets",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "XdrLastSeenAt",
                table: "assets",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "XdrOperationalStatus",
                table: "assets",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "XdrStatus",
                table: "assets",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "xdr_endpoints",
                columns: table => new
                {
                    AgentId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    HostName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    AgentStatus = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    OperationalStatus = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    AgentType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Ip = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Users = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    CollectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_xdr_endpoints", x => x.AgentId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_xdr_endpoints_HostName",
                table: "xdr_endpoints",
                column: "HostName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "xdr_endpoints");

            migrationBuilder.DropColumn(
                name: "InXdr",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "XdrAgentType",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "XdrByNameOnly",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "XdrIp",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "XdrLastSeenAt",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "XdrOperationalStatus",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "XdrStatus",
                table: "assets");
        }
    }
}
