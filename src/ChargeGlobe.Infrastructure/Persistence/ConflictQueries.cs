using System.Text.Json;
using ChargeGlobe.Application.Conflicts;
using ChargeGlobe.Domain.Provenance;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Infrastructure.Persistence;

/// <summary>Conflicts of <c>source_conflict</c> with a label of the location or EVSE they concern.</summary>
public sealed class ConflictQueries(AppDbContext db) : IConflictQueries
{
    public async Task<ConflictList> ListAsync(bool resolved, int limit, CancellationToken cancellationToken)
    {
        var conflicts = await db.SourceConflicts.AsNoTracking()
            .Where(conflict => (conflict.ResolvedAt != null) == resolved)
            .OrderByDescending(conflict => conflict.DetectedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
        List<Guid> ids = [.. conflicts.Select(conflict => conflict.EntityId).OfType<Guid>().Distinct()];
        var locations = await db.Locations.AsNoTracking()
            .Where(location => ids.Contains(location.Id))
            .ToDictionaryAsync(location => location.Id, location => string.Join(", ", new[] { location.Name, location.Address, location.City }.Where(part => !string.IsNullOrWhiteSpace(part))), cancellationToken);
        var evses = await db.Evses.AsNoTracking()
            .Where(evse => ids.Contains(evse.Id))
            .ToDictionaryAsync(evse => evse.Id, evse => evse.EvseIdDisplay, cancellationToken);
        var open = await db.SourceConflicts.CountAsync(conflict => conflict.ResolvedAt == null, cancellationToken);
        return new ConflictList(
            [.. conflicts.Select(conflict => new ConflictView(
                conflict.Id,
                conflict.EntityType.ToString(),
                conflict.EntityId,
                conflict.EntityId is { } id ? (conflict.EntityType == EntityType.EVSE ? evses : locations).GetValueOrDefault(id) : null,
                conflict.Attribute,
                JsonDocument.Parse(conflict.Values).RootElement.Clone(),
                conflict.DetectedAt,
                conflict.ResolvedAt,
                conflict.Resolution))],
            open);
    }
}
