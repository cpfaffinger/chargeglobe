using ChargeGlobe.Application.Locations;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Infrastructure.Persistence;

/// <summary>Operators by number of active locations, from <c>operator</c> and <c>location</c>.</summary>
public sealed class OperatorQueries(AppDbContext db) : IOperatorQueries
{
    public async Task<IReadOnlyList<OperatorSummary>> ListAsync(string? query, IReadOnlyList<Guid> ids, int limit, CancellationToken cancellationToken)
    {
        var pattern = query is null ? null : LikePattern.Contains(query);
        Guid[] wanted = [.. ids];
        var rows = await db.Database
            .SqlQuery<OperatorRow>($"""
                SELECT o.id AS "Id", o.name AS "Name", o.operator_id AS "OperatorId", count(l.id)::int AS "Locations"
                FROM operator o
                JOIN location l ON l.operator_id = o.id AND l.lifecycle = 'ACTIVE'
                WHERE ({pattern}::text IS NULL OR o.name ILIKE {pattern}::text OR o.operator_id ILIKE {pattern}::text)
                  AND (cardinality({wanted}::uuid[]) = 0 OR o.id = ANY({wanted}::uuid[]))
                GROUP BY o.id, o.name, o.operator_id
                ORDER BY count(l.id) DESC, o.name, o.id
                LIMIT {limit}
                """)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(row => new OperatorSummary(row.Id, row.Name, row.OperatorId, row.Locations))];
    }

    /// <summary>One row of the query; property names are the column aliases.</summary>
    private sealed class OperatorRow
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = null!;

        public string? OperatorId { get; set; }

        public int Locations { get; set; }
    }
}
