using ChargeGlobe.Application.Locations;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Infrastructure.Persistence;

/// <summary>
/// Map read model on PostGIS: bounding box via the GiST index on <c>location.geom</c>, nearest to the
/// centre of the box first, or a corridor along a route in the order of the route; aggregated with the
/// current status of the active EVSEs.
/// </summary>
public sealed class LocationQueries(AppDbContext db, IAppConfig config, TimeProvider timeProvider) : ILocationQueries
{
    private static readonly string[] OccupiedStatuses = [nameof(EvseStatus.CHARGING), nameof(EvseStatus.BLOCKED), nameof(EvseStatus.RESERVED)];

    private static readonly string[] FaultedStatuses = [nameof(EvseStatus.OUTOFORDER), nameof(EvseStatus.INOPERATIVE)];

    private static readonly string[] AdHocMethods = [.. AuthMethods.AdHoc.Select(method => method.ToString())];

    private static readonly LocationFilter NoFilter = new([], null, null, null, [], false, false, false, false, false);

    /// <summary>Stand-in line of the queries without route; the route condition is switched off for them.</summary>
    private static readonly GeoPoint[] NoRoute = [new(0, 0), new(1, 1)];

    public async Task<LocationPage> InBoundingBoxAsync(BoundingBox box, LocationFilter filter, int limit, CancellationToken cancellationToken)
    {
        var rows = await SummariesAsync(box, null, filter, limit + 1, cancellationToken);
        var locations = rows.Take(limit).Select(row => row.ToSummary()).ToList();
        return new LocationPage(locations, rows.Count > limit, await AttributionAsync([.. locations.Select(location => location.Id)], cancellationToken));
    }

    public async Task<RoutePage> AlongRouteAsync(
        IReadOnlyList<GeoPoint> route, int corridorMeters, LocationFilter filter, int limit, CancellationToken cancellationToken)
    {
        var rows = await SummariesAsync(null, null, filter, limit + 1, cancellationToken, route, corridorMeters);
        var locations = rows.Take(limit).Select(row => RouteLocation.From(row.ToSummary(), row.DistanceFromRoute!.Value, row.PositionOnRoute!.Value)).ToList();
        return new RoutePage(locations, rows.Count > limit, await AttributionAsync([.. locations.Select(location => location.Id)], cancellationToken));
    }

    public async Task<LocationPage> ByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        var rows = await SummariesAsync(null, [.. ids], NoFilter, ids.Count, cancellationToken);
        var locations = rows.Select(row => row.ToSummary()).ToList();
        return new LocationPage(locations, false, await AttributionAsync([.. locations.Select(location => location.Id)], cancellationToken));
    }

    /// <summary>
    /// Active locations with the counts of their EVSEs: in the box (nearest to its centre first), with one of the ids, or
    /// within <paramref name="corridorMeters"/> of the route (in the order of the route), matching the filter, at most
    /// <paramref name="take"/>. The position on the route is the share of the line (in degrees) up to the closest point
    /// times its length in metres – close enough to order the stops and tell the kilometre.
    /// </summary>
    private Task<List<SummaryRow>> SummariesAsync(
        BoundingBox? within, Guid[]? ids, LocationFilter filter, int take, CancellationToken cancellationToken,
        IReadOnlyList<GeoPoint>? route = null, int corridorMeters = 0)
    {
        var anyRoute = route is null;
        var line = route ?? NoRoute;
        double[] longitudes = [.. line.Select(point => point.Longitude)];
        double[] latitudes = [.. line.Select(point => point.Latitude)];
        var box = within ?? new BoundingBox(-180, -90, 180, 90);
        var anyBox = within is null;
        var anyId = ids is null;
        Guid[] wanted = ids ?? [];
        var fresh = FreshSince();
        string[] connectors = [.. filter.Connectors];
        var operatorPattern = filter.Operator is null ? null : LikePattern.Contains(filter.Operator);
        Guid[] operatorIds = [.. filter.OperatorIds];
        var centerLongitude = (box.MinLongitude + box.MaxLongitude) / 2;
        var centerLatitude = (box.MinLatitude + box.MaxLatitude) / 2;
        return db.Database
            .SqlQuery<SummaryRow>($"""
                WITH route AS MATERIALIZED (
                    SELECT ST_SetSRID(ST_MakeLine(ARRAY(
                        SELECT ST_MakePoint(p.x, p.y)
                        FROM unnest({longitudes}::float8[], {latitudes}::float8[]) WITH ORDINALITY AS p(x, y, n)
                        ORDER BY p.n)), 4326) AS g)
                SELECT l.id AS "Id", l.name AS "Name", l.address AS "Address", l.postal_code AS "PostalCode", l.city AS "City",
                       l.country AS "Country", ST_Y(l.geom::geometry) AS "Latitude", ST_X(l.geom::geometry) AS "Longitude",
                       o.name AS "Operator",
                       count(e.id)::int AS "EvsesTotal",
                       (count(e.id) FILTER (WHERE c.status = 'AVAILABLE' AND c.observed_at >= {fresh}))::int AS "EvsesAvailable",
                       (count(e.id) FILTER (WHERE c.status = ANY({OccupiedStatuses}) AND c.observed_at >= {fresh}))::int AS "EvsesOccupied",
                       (count(e.id) FILTER (WHERE c.status = ANY({FaultedStatuses}) AND c.observed_at >= {fresh}))::int AS "EvsesFaulted",
                       max(e.max_power_kw) AS "MaxPowerKw",
                       max(e.max_power_kw) FILTER (WHERE c.status = 'AVAILABLE' AND c.observed_at >= {fresh}) AS "MaxAvailablePowerKw",
                       max(c.observed_at) AS "StatusObservedAt",
                       l.green_energy AS "GreenEnergy", l.free_parking AS "FreeParking", l.roofed_parking AS "RoofedParking",
                       l.barrier_free_parking_spaces AS "BarrierFreeParkingSpaces", l.price_url AS "PriceUrl",
                       CASE WHEN {anyRoute} THEN NULL ELSE ST_Distance(l.geom, (SELECT g FROM route)::geography) END AS "DistanceFromRoute",
                       CASE WHEN {anyRoute} THEN NULL
                            ELSE ST_LineLocatePoint((SELECT g FROM route), l.geom::geometry) * ST_Length((SELECT g FROM route)::geography) END AS "PositionOnRoute"
                FROM location l
                LEFT JOIN operator o ON o.id = l.operator_id
                LEFT JOIN evse e ON e.location_id = l.id AND e.lifecycle = 'ACTIVE'
                LEFT JOIN evse_status_current c ON c.evse_id = e.id
                WHERE l.lifecycle = 'ACTIVE'
                  AND ({anyBox} OR l.geom && ST_MakeEnvelope({box.MinLongitude}, {box.MinLatitude}, {box.MaxLongitude}, {box.MaxLatitude}, 4326)::geography)
                  AND ({anyId} OR l.id = ANY({wanted}::uuid[]))
                  AND ({anyRoute} OR ST_DWithin(l.geom, (SELECT g FROM route)::geography, {corridorMeters}))
                  AND ({operatorPattern}::text IS NULL OR o.name ILIKE {operatorPattern}::text)
                  AND (cardinality({operatorIds}::uuid[]) = 0 OR l.operator_id = ANY({operatorIds}::uuid[]))
                  AND (NOT {filter.GreenEnergy} OR l.green_energy)
                  AND (NOT {filter.BarrierFree} OR l.barrier_free_parking_spaces > 0)
                  AND (NOT {filter.FiltersEvses} OR EXISTS (
                        SELECT 1 FROM evse fe
                        WHERE fe.location_id = l.id AND fe.lifecycle = 'ACTIVE'
                          AND ({filter.MinPowerKw}::numeric IS NULL OR fe.max_power_kw >= {filter.MinPowerKw}::numeric)
                          AND ({filter.MaxPowerKw}::numeric IS NULL OR fe.max_power_kw <= {filter.MaxPowerKw}::numeric)
                          AND (NOT {filter.AdHoc} OR fe.auth_methods && {AdHocMethods})
                          AND (NOT {filter.Roaming} OR fe.roaming)
                          AND (cardinality({connectors}::text[]) = 0 OR EXISTS (
                                SELECT 1 FROM connector fc WHERE fc.evse_id = fe.id AND fc.standard = ANY({connectors}::text[])))))
                GROUP BY l.id, o.name
                HAVING NOT {filter.AvailableOnly} OR count(e.id) FILTER (WHERE c.status = 'AVAILABLE' AND c.observed_at >= {fresh}) > 0
                ORDER BY CASE WHEN {anyRoute} THEN 0 ELSE ST_LineLocatePoint((SELECT g FROM route), l.geom::geometry) END,
                         l.geom <-> ST_SetSRID(ST_MakePoint({centerLongitude}, {centerLatitude}), 4326)::geography, l.id
                LIMIT {take}
                """)
            .ToListAsync(cancellationToken);
    }

    public async Task<LocationDetail?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var location = await db.Locations.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id && item.Lifecycle == Lifecycle.ACTIVE, cancellationToken);
        if (location is null)
        {
            return null;
        }

        var operatorName = location.OperatorId is { } operatorId
            ? await db.Operators.Where(item => item.Id == operatorId).Select(item => item.Name).FirstOrDefaultAsync(cancellationToken)
            : null;
        var evses = await db.Evses.AsNoTracking()
            .Include(evse => evse.Connectors)
            .Where(evse => evse.LocationId == id && evse.Lifecycle == Lifecycle.ACTIVE)
            .OrderBy(evse => evse.EvseIdNorm)
            .ToListAsync(cancellationToken);
        var evseIds = evses.Select(evse => evse.Id).ToList();
        var statuses = await db.EvseStatusCurrent.AsNoTracking()
            .Where(current => evseIds.Contains(current.EvseId))
            .ToDictionaryAsync(current => current.EvseId, cancellationToken);
        var fresh = FreshSince();
        string StatusOf(Guid evseId) =>
            statuses.TryGetValue(evseId, out var current) && current.ObservedAt >= fresh ? current.Status.ToString() : nameof(EvseStatus.UNKNOWN);

        var details = evses.Select(evse => new EvseDetail(
                evse.Id,
                evse.EvseIdDisplay,
                evse.IdentityKind == IdentityKind.EVSE_ID,
                StatusOf(evse.Id),
                statuses.GetValueOrDefault(evse.Id)?.Since,
                statuses.GetValueOrDefault(evse.Id)?.ObservedAt,
                evse.MaxPowerKw,
                evse.PowerType?.ToString(),
                [.. evse.Connectors.OrderBy(connector => connector.Ordinal).Select(connector => new ConnectorDetail(
                    connector.Standard.ToString(), connector.Format?.ToString(), connector.PowerType?.ToString(), connector.MaxPowerKw))],
                [.. evse.AuthMethods.Select(method => method.ToString())],
                evse.Roaming))
            .ToList();
        var summary = new LocationSummary(
            location.Id,
            location.Name,
            location.Address,
            location.PostalCode,
            location.City,
            location.Country,
            location.Coordinates.Latitude,
            location.Coordinates.Longitude,
            operatorName,
            details.Count,
            details.Count(evse => evse.Status == nameof(EvseStatus.AVAILABLE)),
            evses.Max(evse => evse.MaxPowerKw),
            statuses.Values.Select(current => (DateTimeOffset?)current.ObservedAt).Max(),
            Availability.Of(
                details.Count(evse => evse.Status == nameof(EvseStatus.AVAILABLE)),
                details.Count(evse => OccupiedStatuses.Contains(evse.Status)),
                details.Count(evse => FaultedStatuses.Contains(evse.Status))),
            new LocationFacilities(location.GreenEnergy, location.FreeParking, location.RoofedParking, location.BarrierFreeParkingSpaces, location.PriceUrl),
            details.Where(evse => evse.Status == nameof(EvseStatus.AVAILABLE)).Max(evse => evse.MaxPowerKw));
        return new LocationDetail(summary, details, await AttributionAsync([id], cancellationToken));
    }

    private DateTimeOffset FreshSince() =>
        timeProvider.GetUtcNow().AddMinutes(-config.Get<int>(SettingsCatalog.MapStatusMaxAgeMinutes));

    /// <summary>The sources with a record for one of the locations, from the table <c>source</c>.</summary>
    private async Task<IReadOnlyList<SourceAttribution>> AttributionAsync(Guid[] locationIds, CancellationToken cancellationToken)
    {
        var keys = await db.SourceRecords
            .Where(record => record.EntityType == EntityType.LOCATION && locationIds.Contains(record.EntityId))
            .Select(record => record.SourceKey)
            .Distinct()
            .ToListAsync(cancellationToken);
        return await db.Sources
            .Where(source => keys.Contains(source.Key))
            .OrderBy(source => source.Key)
            .Select(source => new SourceAttribution(source.Key, source.AttributionText, source.License))
            .ToListAsync(cancellationToken);
    }

    /// <summary>One row of the bounding box query; property names are the column aliases.</summary>
    private sealed class SummaryRow
    {
        public Guid Id { get; set; }

        public string? Name { get; set; }

        public string? Address { get; set; }

        public string? PostalCode { get; set; }

        public string? City { get; set; }

        public string Country { get; set; } = null!;

        public double Latitude { get; set; }

        public double Longitude { get; set; }

        public string? Operator { get; set; }

        public int EvsesTotal { get; set; }

        public int EvsesAvailable { get; set; }

        public int EvsesOccupied { get; set; }

        public int EvsesFaulted { get; set; }

        public decimal? MaxPowerKw { get; set; }

        public decimal? MaxAvailablePowerKw { get; set; }

        public DateTimeOffset? StatusObservedAt { get; set; }

        public bool? GreenEnergy { get; set; }

        public bool? FreeParking { get; set; }

        public bool? RoofedParking { get; set; }

        public int? BarrierFreeParkingSpaces { get; set; }

        public string? PriceUrl { get; set; }

        public double? DistanceFromRoute { get; set; }

        public double? PositionOnRoute { get; set; }

        public LocationSummary ToSummary() =>
            new(Id, Name, Address, PostalCode, City, Country, Latitude, Longitude, Operator, EvsesTotal, EvsesAvailable, MaxPowerKw,
                StatusObservedAt, Availability.Of(EvsesAvailable, EvsesOccupied, EvsesFaulted),
                new LocationFacilities(GreenEnergy, FreeParking, RoofedParking, BarrierFreeParkingSpaces, PriceUrl), MaxAvailablePowerKw);
    }
}
