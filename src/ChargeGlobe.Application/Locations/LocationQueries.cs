using ChargeGlobe.Domain;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Application.Locations;

/// <summary>Summary of the current status of a location's EVSEs, for the map colour.</summary>
public static class Availability
{
    /// <summary>At least one EVSE is free.</summary>
    public const string Available = "AVAILABLE";

    /// <summary>None free, at least one charging, blocked or reserved.</summary>
    public const string Occupied = "OCCUPIED";

    /// <summary>All EVSEs with a current status are out of order or inoperative.</summary>
    public const string OutOfOrder = "OUT_OF_ORDER";

    /// <summary>No EVSE has a status younger than <c>map.status_max_age_minutes</c>.</summary>
    public const string Unknown = "UNKNOWN";

    /// <summary>OCPI statuses that count as occupied: charging, blocked, reserved.</summary>
    public static readonly string[] OccupiedStatuses = ["CHARGING", "BLOCKED", "RESERVED"];

    /// <summary>OCPI statuses that count as out of order.</summary>
    public static readonly string[] FaultedStatuses = ["OUTOFORDER", "INOPERATIVE"];

    public static string Of(int available, int occupied, int outOfOrder) =>
        available > 0 ? Available : occupied > 0 ? Occupied : outOfOrder > 0 ? OutOfOrder : Unknown;
}

/// <summary>A location aggregated for map markers.</summary>
/// <param name="StatusObservedAt">Newest status reading of its EVSEs; null if none.</param>
/// <param name="Availability">One of the <see cref="Locations.Availability"/> values.</param>
/// <param name="Facilities">Amenities and price page; each value null if no source knows it.</param>
/// <param name="MaxAvailablePowerKw">Highest power among the EVSEs that are AVAILABLE now; null if none is.</param>
/// <param name="EvsesOccupied">EVSEs charging, blocked or reserved now.</param>
/// <param name="EvsesFaulted">EVSEs out of order or inoperative now; the rest of <paramref name="EvsesTotal"/> has no current status.</param>
/// <param name="ParkId">
/// Charging park the location belongs to: locations of the same operator next to each other (<c>location.park_radius_meters</c>),
/// e.g. two charging stations the source lists separately at one spot; null if the location stands alone.
/// </param>
/// <param name="ParkLocations">Locations in its park, itself included; 1 if it stands alone.</param>
public sealed record LocationSummary(
    Guid Id,
    string? Name,
    string? Address,
    string? PostalCode,
    string? City,
    string Country,
    double Latitude,
    double Longitude,
    string? Operator,
    int EvsesTotal,
    int EvsesAvailable,
    decimal? MaxPowerKw,
    DateTimeOffset? StatusObservedAt,
    string Availability,
    LocationFacilities Facilities,
    decimal? MaxAvailablePowerKw,
    int EvsesOccupied,
    int EvsesFaulted,
    Guid? ParkId = null,
    int ParkLocations = 1);

/// <param name="GreenEnergy">The operator states 100 % renewable energy.</param>
/// <param name="FreeParking">Parking is free of charge while charging.</param>
/// <param name="RoofedParking">The parking spaces are covered.</param>
/// <param name="BarrierFreeParkingSpaces">Number of barrier-free parking spaces.</param>
/// <param name="PriceUrl">Page of the operator with the prices.</param>
public sealed record LocationFacilities(bool? GreenEnergy, bool? FreeParking, bool? RoofedParking, int? BarrierFreeParkingSpaces, string? PriceUrl);

/// <summary>Attribution of a source whose data is part of a response (table <c>source</c>).</summary>
public sealed record SourceAttribution(string Source, string Text, string License);

/// <summary>A location along a route: the summary of the map plus where it lies relative to the route.</summary>
/// <param name="DistanceFromRouteMeters">Shortest distance to the line of the route.</param>
/// <param name="PositionOnRouteMeters">Distance from the start along the line to the point closest to the location.</param>
public sealed record RouteLocation(
    Guid Id,
    string? Name,
    string? Address,
    string? PostalCode,
    string? City,
    string Country,
    double Latitude,
    double Longitude,
    string? Operator,
    int EvsesTotal,
    int EvsesAvailable,
    decimal? MaxPowerKw,
    DateTimeOffset? StatusObservedAt,
    string Availability,
    LocationFacilities Facilities,
    decimal? MaxAvailablePowerKw,
    double DistanceFromRouteMeters,
    double PositionOnRouteMeters)
{
    public static RouteLocation From(LocationSummary location, double distance, double position) =>
        new(location.Id, location.Name, location.Address, location.PostalCode, location.City, location.Country, location.Latitude,
            location.Longitude, location.Operator, location.EvsesTotal, location.EvsesAvailable, location.MaxPowerKw,
            location.StatusObservedAt, location.Availability, location.Facilities, location.MaxAvailablePowerKw, distance, position);
}

/// <param name="Truncated">More locations lie in the corridor than were returned.</param>
public sealed record RoutePage(IReadOnlyList<RouteLocation> Locations, bool Truncated, IReadOnlyList<SourceAttribution> Attribution);

/// <param name="Truncated">More locations lie in the box than were returned.</param>
public sealed record LocationPage(IReadOnlyList<LocationSummary> Locations, bool Truncated, IReadOnlyList<SourceAttribution> Attribution);

/// <param name="Total">All matching locations in the area, also those beyond the limit.</param>
public sealed record AreaPage(IReadOnlyList<LocationSummary> Locations, int Total, bool Truncated, IReadOnlyList<SourceAttribution> Attribution);

public sealed record ConnectorDetail(string Standard, string? Format, string? PowerType, decimal? MaxPowerKw);

/// <param name="EvseId">EVSE-ID with separators; for a surrogate the chargeglobe ID <c>XX*CG*E…</c>.</param>
/// <param name="Status">OCPI status, or <c>UNKNOWN</c> if there is none or it is older than the allowed age.</param>
/// <param name="StatusSource">Key of the source that reported the current status (table <c>source</c>); null with <c>UNKNOWN</c>.</param>
/// <param name="AuthMethods">Ways to start and pay a session (<c>AuthMethod</c> names); empty if unknown.</param>
/// <param name="Roaming">Charging cards of other providers are accepted; null if unknown.</param>
/// <param name="ChargingStation">Label of the charging station (pillar, wallbox) of the EVSE as a source names it, e.g. <c>AT*MER*E006566</c>; null if unknown.</param>
public sealed record EvseDetail(
    Guid Id,
    string EvseId,
    bool HasEvseId,
    string Status,
    DateTimeOffset? StatusSince,
    DateTimeOffset? StatusObservedAt,
    string? StatusSource,
    decimal? MaxPowerKw,
    string? PowerType,
    IReadOnlyList<ConnectorDetail> Connectors,
    IReadOnlyList<string> AuthMethods,
    bool? Roaming,
    string? ChargingStation = null);

/// <param name="Park">The other locations of its charging park, nearest first; empty if it stands alone.</param>
public sealed record LocationDetail(
    LocationSummary Location, IReadOnlyList<EvseDetail> Evses, IReadOnlyList<SourceAttribution> Attribution, IReadOnlyList<LocationSummary> Park);

/// <param name="Locations">Locations whose EVSEs are listed (1, or the locations of the park).</param>
public sealed record LocationEvses(int Locations, IReadOnlyList<Guid> EvseIds);

/// <summary>
/// Charging parks: active locations of one operator within <c>location.park_radius_meters</c> of each other (also
/// through a chain of them) share a park id (columns <c>location.park_id</c>, <c>location.park_locations</c>).
/// </summary>
public interface ILocationParks
{
    /// <summary>Recomputes the parks; returns the number of locations whose park changed.</summary>
    Task<int> RefreshAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Filters of the map. Connector, power, ad-hoc payment and roaming must hold for the same active EVSE; the counts of a
/// matching location still cover all its EVSEs. A false flag means "no filter".
/// </summary>
/// <param name="Connectors">OCPI connector standards; one of them is enough. Empty: any.</param>
/// <param name="MinPowerKw">Lowest allowed maximum power of the EVSE.</param>
/// <param name="MaxPowerKw">Highest allowed maximum power of the EVSE.</param>
/// <param name="Operator">Part of the operator name, case-insensitive.</param>
/// <param name="OperatorIds">Operators of the location; one of them is enough. Empty: any.</param>
/// <param name="AvailableOnly">At least one EVSE is AVAILABLE with a current status.</param>
/// <param name="AdHoc">The EVSE can be paid ad hoc (<c>AuthMethods.AdHoc</c>).</param>
/// <param name="Roaming">The EVSE accepts charging cards of other providers.</param>
/// <param name="GreenEnergy">The location is supplied with 100 % renewable energy.</param>
/// <param name="BarrierFree">The location has barrier-free parking spaces.</param>
public sealed record LocationFilter(
    IReadOnlyList<string> Connectors,
    decimal? MinPowerKw,
    decimal? MaxPowerKw,
    string? Operator,
    IReadOnlyList<Guid> OperatorIds,
    bool AvailableOnly,
    bool AdHoc,
    bool Roaming,
    bool GreenEnergy,
    bool BarrierFree)
{
    /// <summary>Whether a condition on the EVSEs of a location applies.</summary>
    public bool FiltersEvses => Connectors.Count > 0 || MinPowerKw is not null || MaxPowerKw is not null || AdHoc || Roaming;
}

/// <summary>Read model of the public map (no permission needed, docs/06 "Standorte").</summary>
public interface ILocationQueries
{
    /// <summary>Active locations in the box that match the filter, nearest to its centre first, at most <paramref name="limit"/>.</summary>
    Task<LocationPage> InBoundingBoxAsync(BoundingBox box, LocationFilter filter, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Active locations within <paramref name="corridorMeters"/> of the line through <paramref name="route"/> that match the
    /// filter, in the order of the route (start first), at most <paramref name="limit"/>.
    /// </summary>
    Task<RoutePage> AlongRouteAsync(IReadOnlyList<GeoPoint> route, int corridorMeters, LocationFilter filter, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Active locations inside the area (GeoJSON MultiPolygon, e.g. a reach) that match the filter, nearest to
    /// <paramref name="center"/> first (null: the centroid of the area), at most <paramref name="limit"/>.
    /// </summary>
    Task<AreaPage> InAreaAsync(string areaGeoJson, GeoPoint? center, LocationFilter filter, int limit, CancellationToken cancellationToken);

    /// <summary>The active locations among <paramref name="ids"/> (e.g. favorites), aggregated like <see cref="InBoundingBoxAsync"/>.</summary>
    Task<LocationPage> ByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken);

    Task<LocationDetail?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// The active EVSEs of an active location, with <paramref name="park"/> those of all locations of its charging park;
    /// null if the location is unknown or inactive.
    /// </summary>
    Task<LocationEvses?> EvsesAsync(Guid id, bool park, CancellationToken cancellationToken);
}
