using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChargeGlobe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdminAndSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ApprovedAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DisabledAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            // Accounts from before the approval count as approved.
            migrationBuilder.Sql("UPDATE \"AspNetUsers\" SET \"ApprovedAt\" = now();");

            // Search (SearchQueries): trigrams over name, street, postal code and city; EVSE-IDs by prefix.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");
            migrationBuilder.Sql(
                "CREATE INDEX ix_location_search ON location USING gin " +
                "((coalesce(name, '') || ' ' || coalesce(address, '') || ' ' || coalesce(postal_code, '') || ' ' || coalesce(city, '')) gin_trgm_ops);");
            migrationBuilder.Sql("CREATE INDEX ix_evse_evse_id_norm_prefix ON evse (evse_id_norm text_pattern_ops);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_evse_evse_id_norm_prefix;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_location_search;");

            migrationBuilder.DropColumn(
                name: "ApprovedAt",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "DisabledAt",
                table: "AspNetUsers");
        }
    }
}
