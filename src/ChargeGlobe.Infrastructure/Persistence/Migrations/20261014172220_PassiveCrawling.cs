using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChargeGlobe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PassiveCrawling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "crawler_run",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_key = table.Column<string>(type: "text", nullable: false),
                    job_type = table.Column<string>(type: "text", nullable: false),
                    target = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    requests = table.Column<int>(type: "integer", nullable: false),
                    records = table.Column<int>(type: "integer", nullable: false),
                    result = table.Column<string>(type: "text", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    cooldown_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crawler_run", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "poll_tile",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_key = table.Column<string>(type: "text", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    latitude = table.Column<double>(type: "double precision", nullable: false),
                    longitude = table.Column<double>(type: "double precision", nullable: false),
                    location_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    priority = table.Column<string>(type: "text", nullable: false),
                    interval_minutes = table.Column<int>(type: "integer", nullable: false),
                    changes_per_day = table.Column<int>(type: "integer", nullable: false),
                    last_polled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    demand_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    leased_by = table.Column<string>(type: "text", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_result = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_poll_tile", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "provider_backoff",
                columns: table => new
                {
                    provider_key = table.Column<string>(type: "text", nullable: false),
                    failures = table.Column<int>(type: "integer", nullable: false),
                    paused_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    blocked = table.Column<bool>(type: "boolean", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_backoff", x => x.provider_key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_crawler_run_provider_key_started_at",
                table: "crawler_run",
                columns: new[] { "provider_key", "started_at" });

            migrationBuilder.CreateIndex(
                name: "IX_crawler_run_started_at",
                table: "crawler_run",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "IX_poll_tile_location_ids",
                table: "poll_tile",
                column: "location_ids")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_poll_tile_provider_key_latitude_longitude",
                table: "poll_tile",
                columns: new[] { "provider_key", "latitude", "longitude" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "crawler_run");

            migrationBuilder.DropTable(
                name: "poll_tile");

            migrationBuilder.DropTable(
                name: "provider_backoff");
        }
    }
}
