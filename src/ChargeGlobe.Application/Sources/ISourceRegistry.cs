using ChargeGlobe.Domain.Provenance;

namespace ChargeGlobe.Application.Sources;

/// <summary>What the descriptor of a source adapter says about its source (table <c>source</c>).</summary>
public sealed record SourceDescription(
    string Key,
    string DisplayName,
    SourceTier Tier,
    string License,
    string AttributionText,
    IReadOnlyList<string> Capabilities);

/// <summary>Keeps the <c>source</c> table in line with the registered adapters.</summary>
public interface ISourceRegistry
{
    /// <summary>Creates missing sources and updates the description of existing ones; idempotent.</summary>
    Task SyncAsync(IReadOnlyCollection<SourceDescription> sources, CancellationToken cancellationToken);
}
