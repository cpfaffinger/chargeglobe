using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChargeGlobe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FaultIntervalIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_evse_status_interval_fault_valid_from",
                table: "evse_status_interval",
                column: "valid_from",
                filter: "status IN ('OUTOFORDER', 'INOPERATIVE')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_evse_status_interval_fault_valid_from",
                table: "evse_status_interval");
        }
    }
}
