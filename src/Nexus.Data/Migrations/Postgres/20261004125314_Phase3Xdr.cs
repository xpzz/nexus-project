using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Data.Migrations.Postgres
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
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "XdrAgentType",
                table: "assets",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "XdrByNameOnly",
                table: "assets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "XdrIp",
                table: "assets",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "XdrLastSeenAt",
                table: "assets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "XdrOperationalStatus",
                table: "assets",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "XdrStatus",
                table: "assets",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "xdr_endpoints",
                columns: table => new
                {
                    AgentId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    HostName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AgentStatus = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OperationalStatus = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AgentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Ip = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Users = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CollectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
