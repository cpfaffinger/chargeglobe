using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChargeGlobe.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Continuous aggregate <c>evse_occupancy_hourly</c> over <c>evse_status_observation</c> (docs/03-datenmodell.md).
    /// Created <c>WITH NO DATA</c>: only that form may run inside a transaction (the migration transaction and the
    /// DO blocks of the idempotent script); <c>WITH DATA</c> would need a statement outside any transaction. Real-time
    /// aggregation (<c>materialized_only = false</c>) answers the range above the watermark from the raw rows, and the
    /// refresh policy materializes the last 7 days every 30 minutes. Older chunks are compressed and do not change.
    /// </summary>
    public partial class OccupancyHourly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE MATERIALIZED VIEW evse_occupancy_hourly
                WITH (timescaledb.continuous, timescaledb.materialized_only = false) AS
                SELECT time_bucket(INTERVAL '1 hour', time) AS bucket,
                       evse_id,
                       count(*) FILTER (WHERE status = 'CHARGING') AS obs_charging,
                       count(*) FILTER (WHERE status = 'AVAILABLE') AS obs_available,
                       count(*) AS obs_total
                FROM evse_status_observation
                GROUP BY bucket, evse_id
                WITH NO DATA;
                """);
            migrationBuilder.Sql(
                "DO $tsdb$ BEGIN PERFORM add_continuous_aggregate_policy('evse_occupancy_hourly', " +
                "start_offset => INTERVAL '7 days', end_offset => INTERVAL '1 hour', schedule_interval => INTERVAL '30 minutes'); END $tsdb$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP MATERIALIZED VIEW evse_occupancy_hourly;");
        }
    }
}
