using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChargeGlobe.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Open <c>evse_id</c> conflicts recorded without the record of the source (before the values carried
    /// <c>source</c>, <c>externalId</c> and <c>locationId</c>) cannot be decided with <c>same</c> and would be
    /// recorded a second time by the next run: they are closed, the next run records them again in full.
    /// </summary>
    public partial class ClosedEvseIdConflicts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE source_conflict
                SET resolved_at = now(),
                    resolution = 'Closed: recorded again with the record of the source by the next run.'
                WHERE resolved_at IS NULL
                  AND attribute = 'evse_id'
                  AND NOT (values ? 'externalId')
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
