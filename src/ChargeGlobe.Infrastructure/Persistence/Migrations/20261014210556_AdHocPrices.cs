using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChargeGlobe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdHocPrices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "evse_price_current",
                columns: table => new
                {
                    evse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_key = table.Column<string>(type: "text", nullable: false),
                    tariff_kind = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    price_per_kwh = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: true),
                    price_per_minute = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: true),
                    session_fee = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    blocking_fee_per_minute = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: true),
                    blocking_after_minutes = table.Column<int>(type: "integer", nullable: true),
                    free_of_charge = table.Column<bool>(type: "boolean", nullable: false),
                    since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evse_price_current", x => new { x.evse_id, x.source_key, x.tariff_kind });
                    table.ForeignKey(
                        name: "FK_evse_price_current_evse_evse_id",
                        column: x => x.evse_id,
                        principalTable: "evse",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "price_observation",
                columns: table => new
                {
                    time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    evse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_key = table.Column<string>(type: "text", nullable: false),
                    tariff_kind = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    price_per_kwh = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: true),
                    price_per_minute = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: true),
                    session_fee = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    blocking_fee_per_minute = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: true),
                    blocking_after_minutes = table.Column<int>(type: "integer", nullable: true),
                    free_of_charge = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_price_observation", x => new { x.evse_id, x.time, x.source_key, x.tariff_kind });
                    table.ForeignKey(
                        name: "FK_price_observation_evse_evse_id",
                        column: x => x.evse_id,
                        principalTable: "evse",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        
            // Price history as TimescaleDB hypertable (monthly chunks), compressed after 30 days and kept without
            // retention: price changes are rare and the history is the point (docs/03-datenmodell.md).
            migrationBuilder.Sql(
                "DO $tsdb$ BEGIN PERFORM create_hypertable('price_observation', by_range('time', INTERVAL '30 days')); END $tsdb$;");
            migrationBuilder.Sql(
                "ALTER TABLE price_observation SET (timescaledb.compress, timescaledb.compress_segmentby = 'evse_id, source_key', " +
                "timescaledb.compress_orderby = 'time DESC');");
            migrationBuilder.Sql("DO $tsdb$ BEGIN PERFORM add_compression_policy('price_observation', INTERVAL '30 days'); END $tsdb$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "evse_price_current");

            migrationBuilder.DropTable(
                name: "price_observation");
        }
    }
}
