using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class Phase7AssetIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Fqdn",
                table: "assets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntuneEnrollmentType",
                table: "assets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntuneOwnerType",
                table: "assets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntuneRegistrationState",
                table: "assets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IntuneSupervised",
                table: "assets",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IpAddresses",
                table: "assets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastUser",
                table: "assets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MacAddresses",
                table: "assets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SccmLastDdrAt",
                table: "assets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SccmLastSwScanAt",
                table: "assets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Uuid",
                table: "assets",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Fqdn",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "IntuneEnrollmentType",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "IntuneOwnerType",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "IntuneRegistrationState",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "IntuneSupervised",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "IpAddresses",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "LastUser",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "MacAddresses",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "SccmLastDdrAt",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "SccmLastSwScanAt",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "Uuid",
                table: "assets");
        }
    }
}
