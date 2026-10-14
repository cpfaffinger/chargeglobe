using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChargeGlobe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EmpTariffs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "emp",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    website = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_emp", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tariff",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    emp_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    price_per_kwh_ac = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: true),
                    price_per_kwh_dc = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: true),
                    price_per_minute = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: true),
                    session_fee = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    blocking_fee_per_minute = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: true),
                    blocking_after_minutes = table.Column<int>(type: "integer", nullable: true),
                    monthly_fee = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    roaming_only = table.Column<bool>(type: "boolean", nullable: false),
                    operator_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false)
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
                name: "IX_emp_key",
                table: "emp",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tariff_emp_id",
                table: "tariff",
                column: "emp_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tariff");

            migrationBuilder.DropTable(
                name: "emp");
        }
    }
}
