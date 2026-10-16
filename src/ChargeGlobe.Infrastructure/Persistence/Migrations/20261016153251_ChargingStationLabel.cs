using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChargeGlobe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChargingStationLabel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_charging_station_location_id",
                table: "charging_station");

            migrationBuilder.CreateIndex(
                name: "IX_charging_station_location_id_label",
                table: "charging_station",
                columns: new[] { "location_id", "label" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_charging_station_location_id_label",
                table: "charging_station");

            migrationBuilder.CreateIndex(
                name: "IX_charging_station_location_id",
                table: "charging_station",
                column: "location_id");
        }
    }
}
