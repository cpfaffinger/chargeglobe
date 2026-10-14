using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ChargeGlobe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChargingDomain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:postgis", ",,")
                .Annotation("Npgsql:PostgresExtension:timescaledb", ",,");

            migrationBuilder.CreateTable(
                name: "attribute_origin",
                columns: table => new
                {
                    entity_type = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attribute = table.Column<string>(type: "text", nullable: false),
                    source_key = table.Column<string>(type: "text", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    value_hash = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attribute_origin", x => new { x.entity_type, x.entity_id, x.attribute });
                });

            migrationBuilder.CreateTable(
                name: "operator",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    country = table.Column<string>(type: "text", nullable: false),
                    operator_id = table.Column<string>(type: "text", nullable: true),
                    website = table.Column<string>(type: "text", nullable: true),
                    is_cpo = table.Column<bool>(type: "boolean", nullable: false),
                    is_emp = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operator", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "raw_snapshot",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    source_key = table.Column<string>(type: "text", nullable: false),
                    job_id = table.Column<string>(type: "text", nullable: true),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    request_fingerprint = table.Column<string>(type: "text", nullable: false),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    http_status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_raw_snapshot", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "region",
                columns: table => new
                {
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    geom = table.Column<MultiPolygon>(type: "geography (multipolygon, 4326)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_region", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "source",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    tier = table.Column<string>(type: "text", nullable: false),
                    license = table.Column<string>(type: "text", nullable: false),
                    attribution_text = table.Column<string>(type: "text", nullable: false),
                    capabilities = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "source_conflict",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_type = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    attribute = table.Column<string>(type: "text", nullable: false),
                    values = table.Column<string>(type: "jsonb", nullable: false),
                    detected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolution = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_conflict", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "source_record",
                columns: table => new
                {
                    source_key = table.Column<string>(type: "text", nullable: false),
                    external_id = table.Column<string>(type: "text", nullable: false),
                    entity_type = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    match_method = table.Column<string>(type: "text", nullable: false),
                    match_confidence = table.Column<double>(type: "double precision", nullable: false),
                    first_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_snapshot_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_record", x => new { x.source_key, x.external_id, x.entity_type });
                });

            migrationBuilder.CreateTable(
                name: "location",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: true),
                    address = table.Column<string>(type: "text", nullable: true),
                    postal_code = table.Column<string>(type: "text", nullable: true),
                    city = table.Column<string>(type: "text", nullable: true),
                    country = table.Column<string>(type: "text", nullable: false),
                    geom = table.Column<Point>(type: "geography (point, 4326)", nullable: false),
                    operator_id = table.Column<Guid>(type: "uuid", nullable: true),
                    region_code = table.Column<string>(type: "text", nullable: true),
                    opening_times = table.Column<string>(type: "jsonb", nullable: true),
                    access = table.Column<string>(type: "text", nullable: false),
                    lifecycle = table.Column<string>(type: "text", nullable: false),
                    first_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_location", x => x.id);
                    table.ForeignKey(
                        name: "FK_location_operator_operator_id",
                        column: x => x.operator_id,
                        principalTable: "operator",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_location_region_region_code",
                        column: x => x.region_code,
                        principalTable: "region",
                        principalColumn: "code",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "charging_station",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "text", nullable: true),
                    physical_reference = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_charging_station", x => x.id);
                    table.ForeignKey(
                        name: "FK_charging_station_location_location_id",
                        column: x => x.location_id,
                        principalTable: "location",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "location_status_observation",
                columns: table => new
                {
                    time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_key = table.Column<string>(type: "text", nullable: false),
                    evses_total = table.Column<int>(type: "integer", nullable: false),
                    evses_available = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_location_status_observation", x => new { x.location_id, x.time, x.source_key });
                    table.ForeignKey(
                        name: "FK_location_status_observation_location_location_id",
                        column: x => x.location_id,
                        principalTable: "location",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "evse",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    evse_id_norm = table.Column<string>(type: "text", nullable: false),
                    evse_id_display = table.Column<string>(type: "text", nullable: false),
                    identity_kind = table.Column<string>(type: "text", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    charging_station_id = table.Column<Guid>(type: "uuid", nullable: true),
                    operator_id = table.Column<Guid>(type: "uuid", nullable: true),
                    max_power_kw = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: true),
                    power_type = table.Column<string>(type: "text", nullable: true),
                    physical_reference = table.Column<string>(type: "text", nullable: true),
                    lifecycle = table.Column<string>(type: "text", nullable: false),
                    first_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evse", x => x.id);
                    table.ForeignKey(
                        name: "FK_evse_charging_station_charging_station_id",
                        column: x => x.charging_station_id,
                        principalTable: "charging_station",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_evse_location_location_id",
                        column: x => x.location_id,
                        principalTable: "location",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_evse_operator_operator_id",
                        column: x => x.operator_id,
                        principalTable: "operator",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "connector",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    evse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    standard = table.Column<string>(type: "text", nullable: false),
                    format = table.Column<string>(type: "text", nullable: false),
                    power_type = table.Column<string>(type: "text", nullable: false),
                    max_voltage = table.Column<int>(type: "integer", nullable: true),
                    max_amperage = table.Column<int>(type: "integer", nullable: true),
                    max_power_kw = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connector", x => x.id);
                    table.ForeignKey(
                        name: "FK_connector_evse_evse_id",
                        column: x => x.evse_id,
                        principalTable: "evse",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "evse_status_current",
                columns: table => new
                {
                    evse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source_key = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evse_status_current", x => x.evse_id);
                    table.ForeignKey(
                        name: "FK_evse_status_current_evse_evse_id",
                        column: x => x.evse_id,
                        principalTable: "evse",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "evse_status_interval",
                columns: table => new
                {
                    evse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    source_key = table.Column<string>(type: "text", nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evse_status_interval", x => new { x.evse_id, x.valid_from });
                    table.ForeignKey(
                        name: "FK_evse_status_interval_evse_evse_id",
                        column: x => x.evse_id,
                        principalTable: "evse",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "evse_status_observation",
                columns: table => new
                {
                    time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    evse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_key = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    source_status_raw = table.Column<string>(type: "text", nullable: true),
                    source_reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    latency_ms = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evse_status_observation", x => new { x.evse_id, x.time, x.source_key });
                    table.ForeignKey(
                        name: "FK_evse_status_observation_evse_evse_id",
                        column: x => x.evse_id,
                        principalTable: "evse",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_charging_station_location_id",
                table: "charging_station",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "IX_connector_evse_id_ordinal",
                table: "connector",
                columns: new[] { "evse_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_evse_charging_station_id",
                table: "evse",
                column: "charging_station_id");

            migrationBuilder.CreateIndex(
                name: "IX_evse_evse_id_norm",
                table: "evse",
                column: "evse_id_norm",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_evse_location_id",
                table: "evse",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "IX_evse_operator_id",
                table: "evse",
                column: "operator_id");

            migrationBuilder.CreateIndex(
                name: "IX_evse_status_interval_evse_id",
                table: "evse_status_interval",
                column: "evse_id",
                unique: true,
                filter: "valid_to IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_location_geom",
                table: "location",
                column: "geom")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "IX_location_operator_id",
                table: "location",
                column: "operator_id");

            migrationBuilder.CreateIndex(
                name: "IX_location_region_code",
                table: "location",
                column: "region_code");

            migrationBuilder.CreateIndex(
                name: "IX_operator_country_operator_id",
                table: "operator",
                columns: new[] { "country", "operator_id" },
                unique: true,
                filter: "operator_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_raw_snapshot_fetched_at",
                table: "raw_snapshot",
                column: "fetched_at");

            migrationBuilder.CreateIndex(
                name: "IX_raw_snapshot_source_key_content_hash",
                table: "raw_snapshot",
                columns: new[] { "source_key", "content_hash" });

            migrationBuilder.CreateIndex(
                name: "IX_region_geom",
                table: "region",
                column: "geom")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "IX_source_conflict_detected_at",
                table: "source_conflict",
                column: "detected_at",
                filter: "resolved_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_source_record_entity_type_entity_id",
                table: "source_record",
                columns: new[] { "entity_type", "entity_id" });

            // Status time series as TimescaleDB hypertables (daily chunks), compressed after 7 days,
            // dropped after 180 days (docs/03-datenmodell.md).
            foreach (var (table, segmentBy) in new[]
                     {
                         ("evse_status_observation", "evse_id, source_key"),
                         ("location_status_observation", "location_id, source_key"),
                     })
            {
                // PERFORM inside DO, so that the statements also run in the blocks of the idempotent script.
                migrationBuilder.Sql(
                    $"DO $tsdb$ BEGIN PERFORM create_hypertable('{table}', by_range('time', INTERVAL '1 day')); END $tsdb$;");
                migrationBuilder.Sql(
                    $"ALTER TABLE {table} SET (timescaledb.compress, timescaledb.compress_segmentby = '{segmentBy}', " +
                    "timescaledb.compress_orderby = 'time DESC');");
                migrationBuilder.Sql(
                    $"DO $tsdb$ BEGIN PERFORM add_compression_policy('{table}', INTERVAL '7 days'); " +
                    $"PERFORM add_retention_policy('{table}', INTERVAL '180 days'); END $tsdb$;");
            }

            // location.region_code follows the coordinates: the region whose polygon covers them.
            migrationBuilder.Sql("""
                CREATE FUNCTION location_assign_region() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    NEW.region_code := (
                        SELECT r.code FROM region r
                        WHERE r.geom IS NOT NULL AND ST_Covers(r.geom, NEW.geom)
                        ORDER BY r.code
                        LIMIT 1);
                    RETURN NEW;
                END
                $$;
                CREATE TRIGGER location_assign_region
                    BEFORE INSERT OR UPDATE OF geom ON location
                    FOR EACH ROW EXECUTE FUNCTION location_assign_region();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER location_assign_region ON location;");
            migrationBuilder.Sql("DROP FUNCTION location_assign_region();");
            migrationBuilder.DropTable(
                name: "attribute_origin");

            migrationBuilder.DropTable(
                name: "connector");

            migrationBuilder.DropTable(
                name: "evse_status_current");

            migrationBuilder.DropTable(
                name: "evse_status_interval");

            migrationBuilder.DropTable(
                name: "evse_status_observation");

            migrationBuilder.DropTable(
                name: "location_status_observation");

            migrationBuilder.DropTable(
                name: "raw_snapshot");

            migrationBuilder.DropTable(
                name: "source");

            migrationBuilder.DropTable(
                name: "source_conflict");

            migrationBuilder.DropTable(
                name: "source_record");

            migrationBuilder.DropTable(
                name: "evse");

            migrationBuilder.DropTable(
                name: "charging_station");

            migrationBuilder.DropTable(
                name: "location");

            migrationBuilder.DropTable(
                name: "operator");

            migrationBuilder.DropTable(
                name: "region");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:postgis", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:timescaledb", ",,");
        }
    }
}
