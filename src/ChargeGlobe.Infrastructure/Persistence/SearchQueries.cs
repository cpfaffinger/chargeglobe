using System.Text.RegularExpressions;
using ChargeGlobe.Application.Locations;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Infrastructure.Persistence;

/// <summary>
/// Search with the trigram indexes of the migration <c>AdminAndSearch</c>: EVSE-IDs by prefix of the normalized form,
/// locations by words in name, street, postal code and city, ordered by trigram similarity.
/// </summary>
public sealed partial class SearchQueries(AppDbContext db) : ISearchQueries
{
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var text = query.Trim();
        var hits = new List<SearchHit>();
        var normalized = Separators().Replace(text, string.Empty).ToUpperInvariant();
        if (EvseIdStart().IsMatch(normalized))
        {
            var prefix = $"{normalized}%";
            hits.AddRange(await db.Database
                .SqlQuery<SearchHit>($"""
                    SELECT 'EVSE' AS "Type", e.evse_id_display AS "Label",
                           concat_ws(', ', l.name, l.address, concat_ws(' ', l.postal_code, l.city)) AS "Detail",
                           l.id AS "LocationId", ST_Y(l.geom::geometry) AS "Latitude", ST_X(l.geom::geometry) AS "Longitude",
                           e.evse_id_display AS "EvseId"
                    FROM evse e JOIN location l ON l.id = e.location_id
                    WHERE e.evse_id_norm LIKE {prefix} AND e.lifecycle = 'ACTIVE' AND l.lifecycle = 'ACTIVE'
                    ORDER BY e.evse_id_norm
                    LIMIT {limit}
                    """)
                .ToListAsync(cancellationToken));
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(LikePattern.Contains)
            .ToArray();
        if (hits.Count < limit && words.Length > 0)
        {
            // The expression equals the one of the index ix_location_search.
            hits.AddRange(await db.Database
                .SqlQuery<SearchHit>($"""
                    SELECT 'LOCATION' AS "Type", coalesce(l.name, l.address, 'Ladestelle') AS "Label",
                           concat_ws(', ', l.address, concat_ws(' ', l.postal_code, l.city)) AS "Detail",
                           l.id AS "LocationId", ST_Y(l.geom::geometry) AS "Latitude", ST_X(l.geom::geometry) AS "Longitude",
                           NULL::text AS "EvseId"
                    FROM location l
                    WHERE l.lifecycle = 'ACTIVE'
                      AND (coalesce(l.name, '') || ' ' || coalesce(l.address, '') || ' ' || coalesce(l.postal_code, '') || ' ' || coalesce(l.city, ''))
                          ILIKE ALL ({words})
                    ORDER BY similarity(coalesce(l.name, '') || ' ' || coalesce(l.address, '') || ' ' || coalesce(l.postal_code, '') || ' ' || coalesce(l.city, ''), {text}) DESC,
                             l.name, l.id
                    LIMIT {limit - hits.Count}
                    """)
                .ToListAsync(cancellationToken));
        }

        return hits;
    }

    [GeneratedRegex(@"[\s*\-_.]")]
    private static partial Regex Separators();

    /// <summary>Country, three-character operator and the <c>E</c> of an EVSE-ID (<c>ATVIEE</c>, <c>AT*VIE*E…</c>).</summary>
    [GeneratedRegex("^[A-Z]{2}[A-Z0-9]{3}E")]
    private static partial Regex EvseIdStart();
}
