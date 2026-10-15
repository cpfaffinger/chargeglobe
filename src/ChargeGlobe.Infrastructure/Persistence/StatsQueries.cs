using ChargeGlobe.Application.Locations;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Application.Stats;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Provenance;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Infrastructure.Persistence;

/// <summary>
/// Counts of the charging infrastructure in a few SQL queries over the active locations (optionally of one region) and
/// their active EVSEs: totals, per region, per power class, per connector standard, top operators, status and freshness.
/// </summary>
public sealed class StatsQueries(AppDbContext db, IAppConfig config, TimeProvider timeProvider) : IStatsQueries
{
    /// <summary>The power classes in their order; classes without EVSEs count 0.</summary>
    public static readonly string[] PowerClasses = ["UP_TO_22", "FROM_23_TO_49", "FROM_50_TO_149", "FROM_150", "UNKNOWN"];

    public const int TopOperatorCount = 10;

    private static readonly string[] OccupiedStatuses = Availability.OccupiedStatuses;

    private static readonly string[] FaultedStatuses = Availability.FaultedStatuses;

    public Task<bool> RegionExistsAsync(string code, CancellationToken cancellationToken) =>
        db.Regions.AnyAsync(region => region.Code == code, cancellationToken);

    public async Task<Stats> GetAsync(string? region, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var fresh = now.AddMinutes(-config.Get<int>(SettingsCatalog.MapStatusMaxAgeMinutes));

        // The same scope for every query: active locations (of the region) and their active EVSEs.
        var totals = await db.Database.SqlQuery<TotalsRow>($"""
            WITH loc AS (SELECT id FROM location WHERE lifecycle = 'ACTIVE' AND ({region}::text IS NULL OR region_code = {region}::text)),
                 ev AS (SELECT e.id FROM evse e JOIN loc ON loc.id = e.location_id WHERE e.lifecycle = 'ACTIVE')
            SELECT (SELECT count(*) FROM loc)::int AS "Locations",
                   (SELECT count(*) FROM ev)::int AS "Evses",
                   (SELECT count(*) FROM connector c JOIN ev ON ev.id = c.evse_id)::int AS "Connectors"
            """).SingleAsync(cancellationToken);

        var regions = await db.Database.SqlQuery<RegionRow>($"""
            WITH loc AS (SELECT id, region_code FROM location WHERE lifecycle = 'ACTIVE' AND ({region}::text IS NULL OR region_code = {region}::text)),
                 ev AS (SELECT loc.region_code FROM evse e JOIN loc ON loc.id = e.location_id WHERE e.lifecycle = 'ACTIVE')
            SELECT r.code AS "Code", r.name AS "Name",
                   (SELECT count(*) FROM loc WHERE loc.region_code = r.code)::int AS "Locations",
                   (SELECT count(*) FROM ev WHERE ev.region_code = r.code)::int AS "Evses"
            FROM region r
            WHERE {region}::text IS NULL OR r.code = {region}::text
            ORDER BY r.code
            """).ToListAsync(cancellationToken);

        var power = await db.Database.SqlQuery<PowerRow>($"""
            WITH loc AS (SELECT id FROM location WHERE lifecycle = 'ACTIVE' AND ({region}::text IS NULL OR region_code = {region}::text))
            SELECT CASE WHEN e.max_power_kw IS NULL THEN 'UNKNOWN'
                        WHEN e.max_power_kw <= 22 THEN 'UP_TO_22'
                        WHEN e.max_power_kw < 50 THEN 'FROM_23_TO_49'
                        WHEN e.max_power_kw < 150 THEN 'FROM_50_TO_149'
                        ELSE 'FROM_150' END AS "Class",
                   count(*)::int AS "Evses"
            FROM evse e JOIN loc ON loc.id = e.location_id
            WHERE e.lifecycle = 'ACTIVE'
            GROUP BY 1
            """).ToListAsync(cancellationToken);

        var connectors = await db.Database.SqlQuery<ConnectorRow>($"""
            WITH loc AS (SELECT id FROM location WHERE lifecycle = 'ACTIVE' AND ({region}::text IS NULL OR region_code = {region}::text))
            SELECT c.standard AS "Standard", count(*)::int AS "Connectors"
            FROM connector c JOIN evse e ON e.id = c.evse_id JOIN loc ON loc.id = e.location_id
            WHERE e.lifecycle = 'ACTIVE'
            GROUP BY c.standard
            ORDER BY 2 DESC, 1
            """).ToListAsync(cancellationToken);

        var operators = await db.Database.SqlQuery<OperatorRow>($"""
            WITH loc AS (SELECT id, operator_id FROM location WHERE lifecycle = 'ACTIVE' AND ({region}::text IS NULL OR region_code = {region}::text))
            SELECT o.id AS "Id", o.name AS "Name", count(*)::int AS "Evses"
            FROM evse e JOIN loc ON loc.id = e.location_id
            JOIN operator o ON o.id = COALESCE(e.operator_id, loc.operator_id)
            WHERE e.lifecycle = 'ACTIVE'
            GROUP BY o.id, o.name
            ORDER BY 3 DESC, 2
            LIMIT {TopOperatorCount}
            """).ToListAsync(cancellationToken);

        var status = await db.Database.SqlQuery<StatusRow>($"""
            WITH loc AS (SELECT id FROM location WHERE lifecycle = 'ACTIVE' AND ({region}::text IS NULL OR region_code = {region}::text))
            SELECT (count(*) FILTER (WHERE c.observed_at >= {fresh} AND c.status = 'AVAILABLE'))::int AS "Available",
                   (count(*) FILTER (WHERE c.observed_at >= {fresh} AND c.status = ANY({OccupiedStatuses})))::int AS "Occupied",
                   (count(*) FILTER (WHERE c.observed_at >= {fresh} AND c.status = ANY({FaultedStatuses})))::int AS "Faulted",
                   (count(*) FILTER (WHERE c.observed_at >= {fresh} AND c.status <> 'AVAILABLE'
                        AND NOT c.status = ANY({OccupiedStatuses}) AND NOT c.status = ANY({FaultedStatuses})))::int AS "Unknown",
                   (count(*) FILTER (WHERE c.evse_id IS NULL OR c.observed_at < {fresh}))::int AS "Stale",
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY extract(epoch FROM ({now} - c.observed_at)) / 60) AS "P50",
                   percentile_cont(0.9) WITHIN GROUP (ORDER BY extract(epoch FROM ({now} - c.observed_at)) / 60) AS "P90"
            FROM evse e JOIN loc ON loc.id = e.location_id
            LEFT JOIN evse_status_current c ON c.evse_id = e.id
            WHERE e.lifecycle = 'ACTIVE'
            """).SingleAsync(cancellationToken);

        var sources = await db.Database.SqlQuery<string>($"""
            SELECT DISTINCT r.source_key AS "Value"
            FROM source_record r JOIN location l ON l.id = r.entity_id
            WHERE r.entity_type = {nameof(EntityType.LOCATION)} AND l.lifecycle = 'ACTIVE' AND ({region}::text IS NULL OR l.region_code = {region}::text)
            """).ToListAsync(cancellationToken);
        var attribution = await db.Sources
            .Where(source => sources.Contains(source.Key))
            .OrderBy(source => source.Key)
            .Select(source => new SourceAttribution(source.Key, source.AttributionText, source.License))
            .ToListAsync(cancellationToken);

        var byClass = power.ToDictionary(row => row.Class, row => row.Evses);
        return new Stats(
            new StatsTotals(totals.Locations, totals.Evses, totals.Connectors),
            [.. regions.Select(row => new RegionCount(row.Code, row.Name, row.Locations, row.Evses))],
            [.. PowerClasses.Select(name => new PowerClassCount(name, byClass.GetValueOrDefault(name)))],
            [.. connectors.Select(row => new ConnectorCount(row.Standard, row.Connectors))],
            [.. operators.Select(row => new OperatorCount(row.Id, row.Name, row.Evses))],
            new StatusCounts(status.Available, status.Occupied, status.Faulted, status.Unknown, status.Stale),
            new Freshness(Round(status.P50), Round(status.P90)),
            now,
            attribution);
    }

    private static double? Round(double? minutes) => minutes is { } value ? Math.Round(value, 1) : null;

    // Rows of the queries; property names are the column aliases.
    private sealed class TotalsRow
    {
        public int Locations { get; set; }

        public int Evses { get; set; }

        public int Connectors { get; set; }
    }

    private sealed class RegionRow
    {
        public string Code { get; set; } = null!;

        public string Name { get; set; } = null!;

        public int Locations { get; set; }

        public int Evses { get; set; }
    }

    private sealed class PowerRow
    {
        public string Class { get; set; } = null!;

        public int Evses { get; set; }
    }

    private sealed class ConnectorRow
    {
        public string Standard { get; set; } = null!;

        public int Connectors { get; set; }
    }

    private sealed class OperatorRow
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = null!;

        public int Evses { get; set; }
    }

    private sealed class StatusRow
    {
        public int Available { get; set; }

        public int Occupied { get; set; }

        public int Faulted { get; set; }

        public int Unknown { get; set; }

        public int Stale { get; set; }

        public double? P50 { get; set; }

        public double? P90 { get; set; }
    }
}
