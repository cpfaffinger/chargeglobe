using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChargeGlobe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddressSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "address_import",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    reference_date = table.Column<DateOnly>(type: "date", nullable: false),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    addresses = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_address_import", x => x.id);
                });

            // The addresses of the geocoder; not in the EF model, because AddressStore replaces the table as a whole.
            // The migration AddressStreets replaces this first layout.
            migrationBuilder.Sql("""
                CREATE TABLE address (
                    id bigint NOT NULL,
                    street text NOT NULL,
                    house_number text NOT NULL,
                    postal_code text NOT NULL,
                    city text NOT NULL,
                    municipality_code text NOT NULL,
                    geom geography(Point, 4326) NOT NULL,
                    search_text text NOT NULL);
                ALTER TABLE address ADD CONSTRAINT pk_address PRIMARY KEY (id);
                CREATE INDEX ix_address_search ON address USING gin (search_text gin_trgm_ops);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS address;");

            migrationBuilder.DropTable(
                name: "address_import");
        }
    }
}
