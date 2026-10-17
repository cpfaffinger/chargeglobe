using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChargeGlobe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NetworkPriceNote : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "price_note",
                table: "charging_network",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "price_url",
                table: "charging_network",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sites",
                table: "charging_network",
                type: "jsonb",
                nullable: false,
                // Existing networks have no sites.
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "price_note",
                table: "charging_network");

            migrationBuilder.DropColumn(
                name: "price_url",
                table: "charging_network");

            migrationBuilder.DropColumn(
                name: "sites",
                table: "charging_network");
        }
    }
}
