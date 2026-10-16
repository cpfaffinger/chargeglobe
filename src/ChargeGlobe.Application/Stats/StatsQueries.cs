using ChargeGlobe.Application.Locations;

namespace ChargeGlobe.Application.Stats;

/// <summary>Active locations, their active EVSEs and the connectors of these.</summary>
/// <param name="Parks">Charging parks among the locations (locations of one operator next to each other).</param>
/// <param name="Stations">Charging stations with active EVSEs, as far as a source names them (docs/03, <c>charging_station</c>).</param>
public sealed record StatsTotals(int Locations, int Evses, int Connectors, int Parks, int Stations);

/// <summary>How many sites (or EVSEs) have a size: <c>Size</c> is one of the classes of the distribution.</summary>
public sealed record SizeCount(string Size, int Count);

/// <summary>Active locations and EVSEs whose location lies in the region.</summary>
public sealed record RegionCount(string Code, string Name, int Locations, int Evses);

/// <summary>
/// EVSEs by their maximum power: <c>UP_TO_22</c>, <c>FROM_23_TO_49</c>, <c>FROM_50_TO_149</c>, <c>FROM_150</c> kW or
/// <c>UNKNOWN</c>.
/// </summary>
public sealed record PowerClassCount(string Class, int Evses);

/// <summary>Connectors of the active EVSEs by OCPI standard.</summary>
public sealed record ConnectorCount(string Standard, int Connectors);

/// <summary>Active EVSEs of an operator (of the EVSE, else of its location).</summary>
public sealed record OperatorCount(Guid Id, string Name, int Evses);

/// <summary>
/// The active EVSEs by their current status. Only statuses newer than the freshness limit count as available, occupied,
/// faulted or unknown; the others (old or never reported) are <paramref name="Stale"/>. The five add up to all EVSEs.
/// </summary>
public sealed record StatusCounts(int Available, int Occupied, int Faulted, int Unknown, int Stale);

/// <summary>Age of the current status of the active EVSEs in minutes (median and 90th percentile); null without any.</summary>
public sealed record Freshness(double? P50Minutes, double? P90Minutes);

public sealed record Stats(
    StatsTotals Totals,
    IReadOnlyList<RegionCount> ByRegion,
    IReadOnlyList<PowerClassCount> ByPowerClass,
    IReadOnlyList<ConnectorCount> ByConnector,
    IReadOnlyList<OperatorCount> TopOperators,
    IReadOnlyList<SizeCount> EvsesPerSite,
    IReadOnlyList<SizeCount> ConnectorsPerEvse,
    StatusCounts Status,
    Freshness Freshness,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<SourceAttribution> Attribution);

/// <summary>Counts of the charging infrastructure (docs/06, "Stammdaten &amp; Meta").</summary>
public interface IStatsQueries
{
    /// <summary>Whether a region with this ISO 3166-2 code exists (table <c>region</c>).</summary>
    Task<bool> RegionExistsAsync(string code, CancellationToken cancellationToken);

    /// <summary>The counts of all active locations, or of those in <paramref name="region"/>.</summary>
    Task<Stats> GetAsync(string? region, CancellationToken cancellationToken);
}
