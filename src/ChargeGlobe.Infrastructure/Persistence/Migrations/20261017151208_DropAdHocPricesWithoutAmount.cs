using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChargeGlobe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropAdHocPricesWithoutAmount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A current ad-hoc price without any amount (not free of charge) was the register's default "startFeeCent: 0",
            // not a price (docs/05): the ingest no longer keeps such prices, the ones kept before go. Their history stays.
            migrationBuilder.Sql("""
                DELETE FROM evse_price_current
                WHERE NOT free_of_charge
                  AND coalesce(price_per_kwh, 0) = 0 AND coalesce(price_per_minute, 0) = 0
                  AND coalesce(session_fee, 0) = 0 AND coalesce(blocking_fee_per_minute, 0) = 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deleted rows are not restored; the next pass of the source would not bring them back either.
        }
    }
}
