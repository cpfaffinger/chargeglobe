using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChargeGlobe.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Charging parks: derived columns of <c>location</c>, maintained by LocationParks (not part of the EF model, like
    /// the address tables).
    /// </summary>
    public partial class LocationParks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE location ADD COLUMN park_id uuid, ADD COLUMN park_locations integer NOT NULL DEFAULT 1;
                CREATE INDEX ix_location_park_id ON location (park_id) WHERE park_id IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS ix_location_park_id;
                ALTER TABLE location DROP COLUMN IF EXISTS park_id, DROP COLUMN IF EXISTS park_locations;
                """);
        }
    }
}
