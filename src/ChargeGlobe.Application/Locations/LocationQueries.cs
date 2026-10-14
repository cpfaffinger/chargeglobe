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

    public static string Of(int available, int occupied, int outOfOrder) =>
        available > 0 ? Available : occupied > 0 ? Occupied : outOfOrder > 0 ? OutOfOrder : Unknown;
}

/// <summary>A location aggregated for map markers.</summary>
/// <param name="StatusObservedAt">Newest status reading of its EVSEs; null if none.</param>
/// <param name="Availability">One of the <see cref="Locations.Availability"/> values.</param>
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
    string Availability);

/// <summary>Attribution of a source whose data is part of a response (table <c>source</c>).</summary>
public sealed record SourceAttribution(string Source, string Text, string License);

/// <param name="Truncated">More locations lie in the box than were returned.</param>
public sealed record LocationPage(IReadOnlyList<LocationSummary> Locations, bool Truncated, IReadOnlyList<SourceAttribution> Attribution);

public sealed record ConnectorDetail(string Standard, string? Format, string? PowerType, decimal? MaxPowerKw);

/// <param name="EvseId">EVSE-ID with separators; for a surrogate the chargeglobe ID <c>XX*CG*E…</c>.</param>
/// <param name="Status">OCPI status, or <c>UNKNOWN</c> if there is none or it is older than the allowed age.</param>
public sealed record EvseDetail(
    Guid Id,
    string EvseId,
    bool HasEvseId,
    string Status,
    DateTimeOffset? StatusSince,
    DateTimeOffset? StatusObservedAt,
    decimal? MaxPowerKw,
    string? PowerType,
    IReadOnlyList<ConnectorDetail> Connectors);

public sealed record LocationDetail(LocationSummary Location, IReadOnlyList<EvseDetail> Evses, IReadOnlyList<SourceAttribution> Attribution);

/// <summary>Read model of the public map (no permission needed, docs/06 "Standorte").</summary>
public interface ILocationQueries
{
    /// <summary>Active locations in the box, nearest to its centre first, at most <paramref name="limit"/>.</summary>
    Task<LocationPage> InBoundingBoxAsync(BoundingBox box, int limit, CancellationToken cancellationToken);

    Task<LocationDetail?> FindAsync(Guid id, CancellationToken cancellationToken);
}
