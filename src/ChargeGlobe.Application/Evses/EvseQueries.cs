using ChargeGlobe.Application.Locations;

namespace ChargeGlobe.Application.Evses;

/// <summary>The location an EVSE belongs to.</summary>
/// <param name="Region">ISO 3166-2 code of the region whose polygon contains the location; null if none is known.</param>
public sealed record EvseLocation(
    Guid Id,
    string? Name,
    string? Address,
    string? PostalCode,
    string? City,
    string Country,
    double Latitude,
    double Longitude,
    string? Region);

/// <param name="OperatorId">Operator part of the EVSE-ID (e.g. <c>EVN</c>), if known.</param>
public sealed record EvseOperator(Guid Id, string Name, string? OperatorId);

/// <summary>Current status as last observed.</summary>
/// <param name="Status">OCPI status; <c>UNKNOWN</c> if no source has reported one yet.</param>
/// <param name="Source">Key of the source of the reading.</param>
/// <param name="Stale">True if the reading is older than <c>map.status_max_age_minutes</c> or missing.</param>
public sealed record EvseCurrentStatus(string Status, DateTimeOffset? Since, DateTimeOffset? ObservedAt, string? Source, bool Stale);

/// <summary>Master data and current status of one EVSE.</summary>
/// <param name="EvseId">EVSE-ID with separators; for a surrogate the chargeglobe ID <c>XX*CG*E…</c>.</param>
/// <param name="HasEvseId">False for a surrogate ID assigned by chargeglobe.</param>
/// <param name="Lifecycle"><c>ACTIVE</c>, <c>PLANNED</c> or <c>REMOVED</c>.</param>
public sealed record EvseView(
    Guid Id,
    string EvseId,
    bool HasEvseId,
    string Lifecycle,
    EvseLocation Location,
    EvseOperator? Operator,
    string? PhysicalReference,
    decimal? MaxPowerKw,
    string? PowerType,
    IReadOnlyList<ConnectorDetail> Connectors,
    EvseCurrentStatus Status,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    DateTimeOffset? LastConfirmedAt);

/// <summary>A period with constant status (table <c>evse_status_interval</c>).</summary>
/// <param name="To">End of the period; null while it lasts.</param>
/// <param name="Confidence">Below 1 if sources contradicted each other.</param>
public sealed record StatusInterval(string Status, DateTimeOffset From, DateTimeOffset? To, string Source, double Confidence);

/// <summary>Observation counts of one hour of the week (continuous aggregate <c>evse_occupancy_hourly</c>).</summary>
/// <param name="Weekday">ISO weekday in the requested time zone: 1 = Monday … 7 = Sunday.</param>
/// <param name="Hour">Hour of the day in the requested time zone, 0–23.</param>
public sealed record OccupancyCell(int Weekday, int Hour, long Charging, long Total);

/// <summary>Read model of the EVSE detail endpoints (permission <c>evse:read</c>, docs/06 "Ladepunkte").</summary>
public interface IEvseQueries
{
    /// <summary>The internal id of an EVSE given by its UUID or its EVSE-ID in any notation; null if unknown.</summary>
    Task<Guid?> ResolveAsync(string evseId, CancellationToken cancellationToken);

    Task<EvseView?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Status intervals overlapping [<paramref name="from"/>, <paramref name="to"/>), oldest first.</summary>
    Task<IReadOnlyList<StatusInterval>> StatusHistoryAsync(
        Guid id, DateTimeOffset from, DateTimeOffset to, string? source, CancellationToken cancellationToken);

    /// <summary>
    /// Observation counts per weekday and hour in [<paramref name="from"/>, <paramref name="to"/>), grouped in the
    /// IANA time zone <paramref name="timeZone"/>; only cells with observations. Null if the time zone is unknown.
    /// </summary>
    Task<IReadOnlyList<OccupancyCell>?> OccupancyAsync(
        Guid id, DateTimeOffset from, DateTimeOffset to, string timeZone, CancellationToken cancellationToken);

    /// <summary>The sources with a record for the EVSE, from the table <c>source</c>.</summary>
    Task<IReadOnlyList<SourceAttribution>> AttributionAsync(Guid id, CancellationToken cancellationToken);
}
