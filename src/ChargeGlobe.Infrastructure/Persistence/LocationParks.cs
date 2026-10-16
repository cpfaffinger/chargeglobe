using ChargeGlobe.Application.Locations;
using ChargeGlobe.Application.Settings;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Infrastructure.Persistence;

/// <summary>
/// Charging parks with <c>ST_ClusterDBSCAN</c> per operator on the positions in metres (ETRS89-LAEA, EPSG 3035): every
/// active location within the radius of another one of its operator joins its park (also through a chain), the park id
/// is the smallest location id in it. Locations alone, inactive or without operator have none. Only changed rows are
/// written, so a run without changes costs one read.
/// </summary>
public sealed class LocationParks(AppDbContext db, IAppConfig config) : ILocationParks
{
    public Task<int> RefreshAsync(CancellationToken cancellationToken)
    {
        var radius = config.Get<int>(SettingsCatalog.LocationParkRadiusMeters);
        return db.Database.ExecuteSqlAsync($"""
            WITH clustered AS (
                SELECT id, operator_id,
                       ST_ClusterDBSCAN(ST_Transform(geom::geometry, 3035), eps := {radius}::float8, minpoints := 2)
                           OVER (PARTITION BY operator_id) AS cluster
                FROM location
                WHERE lifecycle = 'ACTIVE' AND operator_id IS NOT NULL AND {radius} > 0),
            park AS (
                SELECT id,
                       CASE WHEN cluster IS NULL THEN NULL
                            ELSE (min(id::text) OVER (PARTITION BY operator_id, cluster))::uuid END AS park_id,
                       CASE WHEN cluster IS NULL THEN 1
                            ELSE (count(*) OVER (PARTITION BY operator_id, cluster))::int END AS park_locations
                FROM clustered),
            target AS (
                SELECT l.id, p.park_id, coalesce(p.park_locations, 1) AS park_locations
                FROM location l LEFT JOIN park p ON p.id = l.id)
            UPDATE location l SET park_id = t.park_id, park_locations = t.park_locations
            FROM target t
            WHERE l.id = t.id AND (l.park_id IS DISTINCT FROM t.park_id OR l.park_locations <> t.park_locations)
            """, cancellationToken);
    }
}
