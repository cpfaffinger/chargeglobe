using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChargeGlobe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EvseAuthAndFacilities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "barrier_free_parking_spaces",
                table: "location",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "free_parking",
                table: "location",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "green_energy",
                table: "location",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "price_url",
                table: "location",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "roofed_parking",
                table: "location",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "auth_methods",
                table: "evse",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<bool>(
                name: "roaming",
                table: "evse",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "barrier_free_parking_spaces",
                table: "location");

            migrationBuilder.DropColumn(
                name: "free_parking",
                table: "location");

            migrationBuilder.DropColumn(
                name: "green_energy",
                table: "location");

            migrationBuilder.DropColumn(
                name: "price_url",
                table: "location");

            migrationBuilder.DropColumn(
                name: "roofed_parking",
                table: "location");

            migrationBuilder.DropColumn(
                name: "auth_methods",
                table: "evse");

            migrationBuilder.DropColumn(
                name: "roaming",
                table: "evse");
        }
    }
}
