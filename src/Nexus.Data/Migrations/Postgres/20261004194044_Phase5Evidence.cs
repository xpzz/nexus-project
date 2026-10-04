using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class Phase5Evidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActivityExplanation",
                table: "assets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActivityLevel",
                table: "assets",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "ActivityScore",
                table: "assets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "AssetType",
                table: "assets",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "DecommissionCandidate",
                table: "assets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "EvidenceJson",
                table: "assets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OperationalState",
                table: "assets",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActivityExplanation",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "ActivityLevel",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "ActivityScore",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "AssetType",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "DecommissionCandidate",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "EvidenceJson",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "OperationalState",
                table: "assets");
        }
    }
}
