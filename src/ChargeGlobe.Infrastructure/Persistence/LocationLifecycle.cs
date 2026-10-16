using ChargeGlobe.Application.Locations;
using ChargeGlobe.Application.Settings;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Infrastructure.Persistence;

/// <summary>
/// <see cref="ILocationLifecycle"/> in two statements. An inventory run counts once it ended OK with records, all its
/// ingest messages are processed and it delivered at least half as many records as the largest of the three runs before
/// it with the same target (a source that answers with a fraction of its data does not remove the rest). A run covers the
/// regions of its target (<c>crawler_run.target</c>, e.g. <c>AT-1,AT-9</c>); a record (location, or EVSE by its location)
/// is missing once that many covering runs started after its <c>last_seen</c>. Locations without region are never
/// missing. Only entities whose state changes are written; PLANNED is left alone.
/// </summary>
public sealed class LocationLifecycle(AppDbContext db, IAppConfig config) : ILocationLifecycle
{
    public async Task<LifecycleChanges> RefreshAsync(CancellationToken cancellationToken)
    {
        var missingRuns = config.Get<int>(SettingsCatalog.LifecycleMissingRuns);
        // Not composed (ToList instead of Single): statements with data-modifying CTEs must stay at the top level.
        var rows = await db.Database.SqlQuery<Counts>($"""
            WITH pending AS MATERIALIZED (
                SELECT DISTINCT job_id FROM ingest_queue WHERE processed_at IS NULL),
            runs AS MATERIALIZED (
                SELECT provider_key, string_to_array(target, ',') AS regions, started_at
                FROM (SELECT r.provider_key, r.target, r.started_at, r.records,
                             max(r.records) OVER (PARTITION BY r.provider_key, r.target ORDER BY r.started_at
                                                  ROWS BETWEEN 3 PRECEDING AND 1 PRECEDING) AS before
                      FROM crawler_run r
                      WHERE r.job_type = 'INVENTORY_FULL' AND r.result = 'OK' AND r.records > 0
                        AND r.id NOT IN (SELECT job_id FROM pending)) counted
                WHERE before IS NULL OR records * 2 >= before),
            records AS (
                SELECT r.entity_type, r.entity_id, r.source_key, r.last_seen, coalesce(l.region_code, el.region_code) AS region
                FROM source_record r
                LEFT JOIN location l ON r.entity_type = 'LOCATION' AND l.id = r.entity_id
                LEFT JOIN evse e ON r.entity_type = 'EVSE' AND e.id = r.entity_id
                LEFT JOIN location el ON el.id = e.location_id
                WHERE r.entity_type IN ('LOCATION', 'EVSE')),
            judged AS MATERIALIZED (
                SELECT entity_type, entity_id,
                       bool_and((SELECT count(*) FROM runs u
                                 WHERE u.provider_key = records.source_key AND u.started_at > records.last_seen
                                   AND records.region = ANY(u.regions)) >= {missingRuns}) AS missing
                FROM records
                GROUP BY entity_type, entity_id),
            locations_removed AS (
                UPDATE location l SET lifecycle = 'REMOVED' FROM judged j
                WHERE j.entity_type = 'LOCATION' AND j.entity_id = l.id AND j.missing AND l.lifecycle = 'ACTIVE'
                RETURNING 1),
            locations_active AS (
                UPDATE location l SET lifecycle = 'ACTIVE' FROM judged j
                WHERE j.entity_type = 'LOCATION' AND j.entity_id = l.id AND NOT j.missing AND l.lifecycle = 'REMOVED'
                RETURNING 1),
            evses_removed AS (
                UPDATE evse e SET lifecycle = 'REMOVED' FROM judged j
                WHERE j.entity_type = 'EVSE' AND j.entity_id = e.id AND j.missing AND e.lifecycle = 'ACTIVE'
                RETURNING 1),
            evses_active AS (
                UPDATE evse e SET lifecycle = 'ACTIVE' FROM judged j
                WHERE j.entity_type = 'EVSE' AND j.entity_id = e.id AND NOT j.missing AND e.lifecycle = 'REMOVED'
                RETURNING 1)
            SELECT (SELECT count(*) FROM locations_removed)::int AS "LocationsRemoved",
                   (SELECT count(*) FROM locations_active)::int AS "LocationsActive",
                   (SELECT count(*) FROM evses_removed)::int AS "EvsesRemoved",
                   (SELECT count(*) FROM evses_active)::int AS "EvsesActive"
            """).ToListAsync(cancellationToken);
        var changes = rows.Single();
        // A station loses its last EVSE when the source moves the EVSE to another station label; removed EVSEs keep theirs.
        var stations = await db.Database.ExecuteSqlAsync(
            $"DELETE FROM charging_station s WHERE NOT EXISTS (SELECT 1 FROM evse e WHERE e.charging_station_id = s.id)",
            cancellationToken);
        return new LifecycleChanges(changes.LocationsRemoved, changes.LocationsActive, changes.EvsesRemoved, changes.EvsesActive, stations);
    }

    /// <summary>One row of the lifecycle statement; property names are the column aliases.</summary>
    private sealed class Counts
    {
        public int LocationsRemoved { get; set; }

        public int LocationsActive { get; set; }

        public int EvsesRemoved { get; set; }

        public int EvsesActive { get; set; }
    }
}
