using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class Phase10Network : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Chassis",
                table: "sccm_devices",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IpAddresses",
                table: "sccm_devices",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MacAddresses",
                table: "sccm_devices",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EthernetMac",
                table: "intune_devices",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WifiMac",
                table: "intune_devices",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Chassis",
                table: "sccm_devices");

            migrationBuilder.DropColumn(
                name: "IpAddresses",
                table: "sccm_devices");

            migrationBuilder.DropColumn(
                name: "MacAddresses",
                table: "sccm_devices");

            migrationBuilder.DropColumn(
                name: "EthernetMac",
                table: "intune_devices");

            migrationBuilder.DropColumn(
                name: "WifiMac",
                table: "intune_devices");
        }
    }
}
