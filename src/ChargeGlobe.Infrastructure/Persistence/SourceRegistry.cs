using ChargeGlobe.Application.Sources;
using ChargeGlobe.Domain.Provenance;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Infrastructure.Persistence;

public sealed class SourceRegistry(AppDbContext db) : ISourceRegistry
{
    public async Task SyncAsync(IReadOnlyCollection<SourceDescription> sources, CancellationToken cancellationToken)
    {
        var keys = sources.Select(source => source.Key).ToList();
        var existing = await db.Sources.Where(source => keys.Contains(source.Key)).ToDictionaryAsync(source => source.Key, cancellationToken);
        foreach (var description in sources)
        {
            if (!existing.TryGetValue(description.Key, out var source))
            {
                source = Source.Create(
                    description.Key, description.DisplayName, description.Tier, description.License, description.AttributionText);
                db.Sources.Add(source);
            }

            source.Describe(
                description.DisplayName, description.Tier, description.License, description.AttributionText, description.Capabilities);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
