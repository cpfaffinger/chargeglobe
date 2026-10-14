namespace ChargeGlobe.Application.Locations;

/// <summary>An operator with the number of its active locations, for the operator filter of the map.</summary>
/// <param name="OperatorId">Operator part of the EVSE-IDs (e.g. <c>VIE</c>), if known.</param>
/// <param name="Locations">Number of active locations of the operator.</param>
public sealed record OperatorSummary(Guid Id, string Name, string? OperatorId, int Locations);

/// <summary>Operators with active locations (public, docs/06 "Betreiber").</summary>
public interface IOperatorQueries
{
    /// <summary>
    /// Operators with at least one active location, most locations first; optionally only those whose name or operator ID
    /// contains <paramref name="query"/> and/or only the given ids; at most <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<OperatorSummary>> ListAsync(string? query, IReadOnlyList<Guid> ids, int limit, CancellationToken cancellationToken);
}
