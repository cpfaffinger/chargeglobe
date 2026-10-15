using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChargeGlobe.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Every run of a source repeated its open conflicts; the merge now records each open conflict once. Removes the
    /// duplicates recorded before, keeping the first detection.
    /// </summary>
    public partial class DeduplicateConflicts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM source_conflict a
                USING source_conflict b
                WHERE a.resolved_at IS NULL
                  AND b.resolved_at IS NULL
                  AND a.attribute = b.attribute
                  AND a.entity_id IS NOT DISTINCT FROM b.entity_id
                  AND a."values" = b."values"
                  AND (a.detected_at, a.id) > (b.detected_at, b.id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The removed rows were duplicates; nothing to restore.
        }
    }
}
