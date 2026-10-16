using System;
using ChargeGlobe.Infrastructure.Addresses;
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
            migrationBuilder.Sql(AddressStore.CreateTableSql("address"));
            migrationBuilder.Sql(AddressStore.CreateIndexesSql("address"));
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
