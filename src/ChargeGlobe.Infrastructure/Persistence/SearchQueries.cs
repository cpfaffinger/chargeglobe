using System.Text.RegularExpressions;
using ChargeGlobe.Application.Locations;
using ChargeGlobe.Application.Settings;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Infrastructure.Persistence;

/// <summary>
/// Search with the trigram indexes of the migration <c>AdminAndSearch</c>: EVSE-IDs by prefix of the normalized form,
/// locations by words in name, street, postal code and city, ordered by trigram similarity. Every hit carries the
/// current counts of its location (and the status of the EVSE), with the freshness limit of the map.
/// </summary>
public sealed partial class SearchQueries(AppDbContext db, IAppConfig config, TimeProvider timeProvider) : ISearchQueries
{
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var text = query.Trim();
        var hits = new List<HitRow>();
        var fresh = timeProvider.GetUtcNow().AddMinutes(-config.Get<int>(SettingsCatalog.MapStatusMaxAgeMinutes));
        string[] occupied = Availability.OccupiedStatuses;
        string[] faulted = Availability.FaultedStatuses;
        var normalized = Separators().Replace(text, string.Empty).ToUpperInvariant();
        if (EvseIdStart().IsMatch(normalized))
        {
            var prefix = $"{normalized}%";
            hits.AddRange(await db.Database
                .SqlQuery<HitRow>($"""
                    SELECT 'EVSE' AS "Type", e.evse_id_display AS "Label",
                           concat_ws(', ', l.name, l.address, concat_ws(' ', l.postal_code, l.city)) AS "Detail",
                           l.id AS "LocationId", ST_Y(l.geom::geometry) AS "Latitude", ST_X(l.geom::geometry) AS "Longitude",
                           e.evse_id_display AS "EvseId",
                           s.total AS "EvsesTotal", s.available AS "EvsesAvailable", s.occupied AS "EvsesOccupied", s.faulted AS "EvsesFaulted",
                           CASE WHEN ce.observed_at >= {fresh} THEN ce.status ELSE 'UNKNOWN' END AS "EvseStatus"
                    FROM evse e JOIN location l ON l.id = e.location_id
                    LEFT JOIN evse_status_current ce ON ce.evse_id = e.id
                    CROSS JOIN LATERAL (
                        SELECT count(e2.id)::int AS total,
                               (count(*) FILTER (WHERE c.status = 'AVAILABLE' AND c.observed_at >= {fresh}))::int AS available,
                               (count(*) FILTER (WHERE c.status = ANY({occupied}) AND c.observed_at >= {fresh}))::int AS occupied,
                               (count(*) FILTER (WHERE c.status = ANY({faulted}) AND c.observed_at >= {fresh}))::int AS faulted
                        FROM evse e2 LEFT JOIN evse_status_current c ON c.evse_id = e2.id
                        WHERE e2.location_id = l.id AND e2.lifecycle = 'ACTIVE') s
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
                .SqlQuery<HitRow>($"""
                    SELECT 'LOCATION' AS "Type", coalesce(l.name, l.address, 'Ladestelle') AS "Label",
                           concat_ws(', ', l.address, concat_ws(' ', l.postal_code, l.city)) AS "Detail",
                           l.id AS "LocationId", ST_Y(l.geom::geometry) AS "Latitude", ST_X(l.geom::geometry) AS "Longitude",
                           NULL::text AS "EvseId",
                           s.total AS "EvsesTotal", s.available AS "EvsesAvailable", s.occupied AS "EvsesOccupied", s.faulted AS "EvsesFaulted",
                           NULL::text AS "EvseStatus"
                    FROM location l
                    CROSS JOIN LATERAL (
                        SELECT count(e2.id)::int AS total,
                               (count(*) FILTER (WHERE c.status = 'AVAILABLE' AND c.observed_at >= {fresh}))::int AS available,
                               (count(*) FILTER (WHERE c.status = ANY({occupied}) AND c.observed_at >= {fresh}))::int AS occupied,
                               (count(*) FILTER (WHERE c.status = ANY({faulted}) AND c.observed_at >= {fresh}))::int AS faulted
                        FROM evse e2 LEFT JOIN evse_status_current c ON c.evse_id = e2.id
                        WHERE e2.location_id = l.id AND e2.lifecycle = 'ACTIVE') s
                    WHERE l.lifecycle = 'ACTIVE'
                      AND (coalesce(l.name, '') || ' ' || coalesce(l.address, '') || ' ' || coalesce(l.postal_code, '') || ' ' || coalesce(l.city, ''))
                          ILIKE ALL ({words})
                    ORDER BY similarity(coalesce(l.name, '') || ' ' || coalesce(l.address, '') || ' ' || coalesce(l.postal_code, '') || ' ' || coalesce(l.city, ''), {text}) DESC,
                             l.name, l.id
                    LIMIT {limit - hits.Count}
                    """)
                .ToListAsync(cancellationToken));
        }

        return [.. hits.Select(hit => hit.ToHit())];
    }

    /// <summary>One row of the queries; property names are the column aliases.</summary>
    private sealed class HitRow
    {
        public string Type { get; set; } = null!;

        public string Label { get; set; } = null!;

        public string? Detail { get; set; }

        public Guid LocationId { get; set; }

        public double Latitude { get; set; }

        public double Longitude { get; set; }

        public string? EvseId { get; set; }

        public int EvsesTotal { get; set; }

        public int EvsesAvailable { get; set; }

        public int EvsesOccupied { get; set; }

        public int EvsesFaulted { get; set; }

        public string? EvseStatus { get; set; }

        public SearchHit ToHit() =>
            new(Type, Label, Detail, LocationId, Latitude, Longitude, EvseId, Availability.Of(EvsesAvailable, EvsesOccupied, EvsesFaulted),
                EvsesTotal, EvsesAvailable, EvsesOccupied, EvseStatus);
    }

    [GeneratedRegex(@"[\s*\-_.]")]
    private static partial Regex Separators();

    /// <summary>Country, three-character operator and the <c>E</c> of an EVSE-ID (<c>ATVIEE</c>, <c>AT*VIE*E…</c>).</summary>
    [GeneratedRegex("^[A-Z]{2}[A-Z0-9]{3}E")]
    private static partial Regex EvseIdStart();
}
