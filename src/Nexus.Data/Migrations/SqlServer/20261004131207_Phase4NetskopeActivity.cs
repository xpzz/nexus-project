using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class Phase4NetskopeActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ActiveSourceCount",
                table: "assets",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ActiveSources",
                table: "assets",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActivityClass",
                table: "assets",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "InNetskope",
                table: "assets",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastStrongActivityAt",
                table: "assets",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NetskopeByNameOnly",
                table: "assets",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NetskopeLastSeenAt",
                table: "assets",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NetskopeStatus",
                table: "assets",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NetskopeVersion",
                table: "assets",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "netskope_clients",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DeviceId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    HostName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    OperatingSystem = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    OsVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Serial = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Manufacturer = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Model = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ClientVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LastEventAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    InstalledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ManagementId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Users = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    CollectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_netskope_clients", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_assets_ActivityClass",
                table: "assets",
                column: "ActivityClass");

            migrationBuilder.CreateIndex(
                name: "IX_netskope_clients_HostName",
                table: "netskope_clients",
                column: "HostName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "netskope_clients");

            migrationBuilder.DropIndex(
                name: "IX_assets_ActivityClass",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "ActiveSourceCount",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "ActiveSources",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "ActivityClass",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "InNetskope",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "LastStrongActivityAt",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "NetskopeByNameOnly",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "NetskopeLastSeenAt",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "NetskopeStatus",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "NetskopeVersion",
                table: "assets");
        }
    }
}
