namespace ChargeGlobe.Application.Locations;

/// <summary>A search hit: a location, or an EVSE with its location.</summary>
/// <param name="Type"><c>EVSE</c> or <c>LOCATION</c>.</param>
/// <param name="Label">EVSE-ID or name of the location.</param>
/// <param name="Detail">Address (and for an EVSE the name of its location).</param>
/// <param name="EvseId">EVSE-ID with separators, for an EVSE hit.</param>
public sealed record SearchHit(string Type, string Label, string? Detail, Guid LocationId, double Latitude, double Longitude, string? EvseId);

/// <summary>Full-text search over the active locations and EVSEs (public, docs/06 "Suche").</summary>
public interface ISearchQueries
{
    /// <summary>
    /// EVSEs whose EVSE-ID starts with the query in any notation, then locations whose name, street, postal code and city
    /// contain every word of the query, the most similar first; at most <paramref name="limit"/> hits.
    /// </summary>
    Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken);
}
