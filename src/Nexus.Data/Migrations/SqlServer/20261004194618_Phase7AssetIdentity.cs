using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Data.Migrations.SqlServer
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
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntuneEnrollmentType",
                table: "assets",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntuneOwnerType",
                table: "assets",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntuneRegistrationState",
                table: "assets",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IntuneSupervised",
                table: "assets",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IpAddresses",
                table: "assets",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastUser",
                table: "assets",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MacAddresses",
                table: "assets",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SccmLastDdrAt",
                table: "assets",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SccmLastSwScanAt",
                table: "assets",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Uuid",
                table: "assets",
                type: "nvarchar(max)",
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
