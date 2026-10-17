using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChargeGlobe.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Fault grace (docs/03, "Status"): the status before the current one, filled from the last closed interval, and the
    /// function evse_shown_status with the same rule as EvseStatusCurrent.InFaultGrace for the map queries.
    /// </summary>
    public partial class FaultGrace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "previous_since",
                table: "evse_status_current",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "previous_status",
                table: "evse_status_current",
                type: "text",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE evse_status_current c
                SET (previous_status, previous_since) = (
                    SELECT i.status, i.valid_from FROM evse_status_interval i
                    WHERE i.evse_id = c.evse_id AND i.valid_to IS NOT NULL
                    ORDER BY i.valid_from DESC LIMIT 1)
                """);

            // Inlined by the planner (one SQL expression); grace_since is now minus the grace, null without grace.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION evse_shown_status(status text, previous_status text, since timestamptz, grace_since timestamptz)
                RETURNS text LANGUAGE sql IMMUTABLE AS $fn$
                    SELECT CASE WHEN grace_since IS NOT NULL AND status IN ('OUTOFORDER', 'INOPERATIVE') AND since > grace_since
                                     AND previous_status IS NOT NULL AND previous_status NOT IN ('OUTOFORDER', 'INOPERATIVE')
                                THEN previous_status ELSE status END
                $fn$
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS evse_shown_status(text, text, timestamptz, timestamptz)");

            migrationBuilder.DropColumn(
                name: "previous_since",
                table: "evse_status_current");

            migrationBuilder.DropColumn(
                name: "previous_status",
                table: "evse_status_current");
        }
    }
}
