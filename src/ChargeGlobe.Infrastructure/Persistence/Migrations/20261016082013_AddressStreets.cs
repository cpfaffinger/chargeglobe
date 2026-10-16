using ChargeGlobe.Infrastructure.Addresses;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChargeGlobe.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Addresses in two tables (streets with trigram indexes, house numbers per street) for the two-stage search. The
    /// tables start empty and the import state is removed, so the worker imports the register again with its next tick.
    /// </summary>
    public partial class AddressStreets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS address;");
            migrationBuilder.Sql(AddressStore.CreateTablesSql());
            migrationBuilder.Sql(AddressStore.CreateIndexesSql());
            migrationBuilder.Sql("DELETE FROM address_import;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TABLE IF EXISTS address;
                DROP TABLE IF EXISTS address_street;
                DELETE FROM address_import;
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
    }
}
