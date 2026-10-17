using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChargeGlobe.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Tariffs as networks, zones and versions (docs/adr/0013): the flat table tariff gives way to charging_network and
    /// tariff_plan (zones as jsonb); providers get their country. There were no tariffs to carry over.
    /// </summary>
    public partial class TariffNetworks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tariff");

            migrationBuilder.AddColumn<string>(
                name: "country",
                table: "emp",
                type: "character varying(2)",
                maxLength: 2,
                nullable: false,
                defaultValue: "AT");

            migrationBuilder.CreateTable(
                name: "charging_network",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    operators = table.Column<List<string>>(type: "text[]", nullable: false),
                    source_key = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_charging_network", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tariff_plan",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    emp_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    monthly_fee = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    prerequisites = table.Column<string>(type: "text", nullable: true),
                    markets = table.Column<List<string>>(type: "text[]", nullable: false),
                    zones = table.Column<string>(type: "jsonb", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    source_key = table.Column<string>(type: "text", nullable: false),
                    source_url = table.Column<string>(type: "text", nullable: false),
                    source_date = table.Column<DateOnly>(type: "date", nullable: true),
                    confidence = table.Column<string>(type: "text", nullable: false),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    content_hash = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tariff_plan", x => x.id);
                    table.ForeignKey(
                        name: "FK_tariff_plan_emp_emp_id",
                        column: x => x.emp_id,
                        principalTable: "emp",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_charging_network_key",
                table: "charging_network",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tariff_plan_emp_id_key_valid_from",
                table: "tariff_plan",
                columns: new[] { "emp_id", "key", "valid_from" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tariff_plan_open",
                table: "tariff_plan",
                column: "source_key",
                filter: "valid_to IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "charging_network");

            migrationBuilder.DropTable(
                name: "tariff_plan");

            migrationBuilder.DropColumn(
                name: "country",
                table: "emp");

            migrationBuilder.CreateTable(
                name: "tariff",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    blocking_after_minutes = table.Column<int>(type: "integer", nullable: true),
                    blocking_fee_per_minute = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    emp_id = table.Column<Guid>(type: "uuid", nullable: false),
                    monthly_fee = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    operator_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    price_per_kwh_ac = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: true),
                    price_per_kwh_dc = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: true),
                    price_per_minute = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: true),
                    roaming_only = table.Column<bool>(type: "boolean", nullable: false),
                    session_fee = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tariff", x => x.id);
                    table.ForeignKey(
                        name: "FK_tariff_emp_emp_id",
                        column: x => x.emp_id,
                        principalTable: "emp",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tariff_emp_id",
                table: "tariff",
                column: "emp_id");
        }
    }
}
