using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ChargeGlobe.Infrastructure.Persistence;

/// <summary>
/// Loads the region polygons (<c>Seed/regions-at.geojson</c> and <c>regions-de.geojson</c>: the Austrian and
/// German states from OpenStreetMap, ODbL, "© OpenStreetMap contributors", built by
/// <c>scripts/regions/build-regions-at.sh</c> and <c>build-regions-de.sh</c>) into the table <c>region</c>.
/// Idempotent: only missing or changed regions are written. If anything changed, the region of every location is
/// recomputed by touching <c>geom</c>, which fires the trigger <c>location_assign_region</c>.
/// </summary>
public sealed class RegionSeeder(AppDbContext db, ILogger<RegionSeeder> logger)
{
    public static readonly IReadOnlyList<string> ResourceNames =
    [
        "ChargeGlobe.Infrastructure.Persistence.Seed.regions-at.geojson",
        "ChargeGlobe.Infrastructure.Persistence.Seed.regions-de.geojson",
    ];

    /// <summary>Time the recomputation of the region of every location may take.</summary>
    public static readonly TimeSpan RecomputeTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Number of inserted or updated regions.</summary>
    public async Task<int> SeedAsync(CancellationToken cancellationToken)
    {
        var changed = 0;
        foreach (var resource in ResourceNames)
        {
            await using var stream = typeof(RegionSeeder).Assembly.GetManifestResourceStream(resource)!;
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            foreach (var feature in document.RootElement.GetProperty("features").EnumerateArray())
            {
                var properties = feature.GetProperty("properties");
                var code = properties.GetProperty("code").GetString()!;
                var name = properties.GetProperty("name").GetString()!;
                var geometry = feature.GetProperty("geometry").GetRawText();
                changed += await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO region (code, name, geom)
                    VALUES ({code}, {name}, ST_Multi(ST_GeomFromGeoJSON({geometry}))::geography)
                    ON CONFLICT (code) DO UPDATE SET name = EXCLUDED.name, geom = EXCLUDED.geom
                    WHERE region.name IS DISTINCT FROM EXCLUDED.name
                       OR ST_AsBinary(region.geom) IS DISTINCT FROM ST_AsBinary(EXCLUDED.geom)
                    """, cancellationToken);
            }
        }

        if (changed > 0)
        {
            // Touching every location fires the region trigger for each of them; with hundreds of thousands of
            // locations that takes minutes, far beyond the default command timeout of 30 s.
            db.Database.SetCommandTimeout(RecomputeTimeout);
            var locations = await db.Database.ExecuteSqlRawAsync("UPDATE location SET geom = geom", cancellationToken);
            logger.LogInformation(
                "Region seed: {Regions} regions inserted or updated, region of {Locations} locations recomputed", changed, locations);
        }

        return changed;
    }
}
