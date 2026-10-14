using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ChargeGlobe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IngestPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_raw_snapshot_source_key_content_hash",
                table: "raw_snapshot");

            migrationBuilder.AlterColumn<string>(
                name: "power_type",
                table: "connector",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "format",
                table: "connector",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.CreateTable(
                name: "ingest_queue",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    provider_key = table.Column<string>(type: "text", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    enqueued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ingest_queue", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "job_lease",
                columns: table => new
                {
                    job_name = table.Column<string>(type: "text", nullable: false),
                    leased_by = table.Column<string>(type: "text", nullable: false),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_lease", x => x.job_name);
                });

            migrationBuilder.CreateTable(
                name: "poll_plan",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_key = table.Column<string>(type: "text", nullable: false),
                    job_type = table.Column<string>(type: "text", nullable: false),
                    region_code = table.Column<string>(type: "text", nullable: false),
                    min_longitude = table.Column<double>(type: "double precision", nullable: false),
                    min_latitude = table.Column<double>(type: "double precision", nullable: false),
                    max_longitude = table.Column<double>(type: "double precision", nullable: false),
                    max_latitude = table.Column<double>(type: "double precision", nullable: false),
                    interval = table.Column<TimeSpan>(type: "interval", nullable: false),
                    next_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    leased_by = table.Column<string>(type: "text", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_result = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_poll_plan", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "provider_rate_budget",
                columns: table => new
                {
                    provider_key = table.Column<string>(type: "text", nullable: false),
                    tokens = table.Column<double>(type: "double precision", nullable: false),
                    refilled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    day_count = table.Column<int>(type: "integer", nullable: false),
                    day_reset_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_rate_budget", x => x.provider_key);
                });

            // The index becomes unique: keep the newest of equal snapshots.
            migrationBuilder.Sql(
                "DELETE FROM raw_snapshot older USING raw_snapshot newer " +
                "WHERE older.source_key = newer.source_key AND older.content_hash = newer.content_hash AND older.id < newer.id;");

            migrationBuilder.CreateIndex(
                name: "IX_raw_snapshot_source_key_content_hash",
                table: "raw_snapshot",
                columns: new[] { "source_key", "content_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ingest_queue_pending",
                table: "ingest_queue",
                column: "id",
                filter: "processed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ingest_queue_processed_at",
                table: "ingest_queue",
                column: "processed_at");

            migrationBuilder.CreateIndex(
                name: "IX_poll_plan_next_run_at",
                table: "poll_plan",
                column: "next_run_at");

            migrationBuilder.CreateIndex(
                name: "IX_poll_plan_provider_key_job_type_region_code",
                table: "poll_plan",
                columns: new[] { "provider_key", "job_type", "region_code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ingest_queue");

            migrationBuilder.DropTable(
                name: "job_lease");

            migrationBuilder.DropTable(
                name: "poll_plan");

            migrationBuilder.DropTable(
                name: "provider_rate_budget");

            migrationBuilder.DropIndex(
                name: "IX_raw_snapshot_source_key_content_hash",
                table: "raw_snapshot");

            migrationBuilder.AlterColumn<string>(
                name: "power_type",
                table: "connector",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "format",
                table: "connector",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_raw_snapshot_source_key_content_hash",
                table: "raw_snapshot",
                columns: new[] { "source_key", "content_hash" });
        }
    }
}
