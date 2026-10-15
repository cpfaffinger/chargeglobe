using System.Text.Json;

namespace ChargeGlobe.Application.Conflicts;

/// <summary>A disagreement between sources the merge did not decide (docs/03, identity resolution).</summary>
/// <param name="EntityType">LOCATION or EVSE.</param>
/// <param name="EntityLabel">Name and address of the location, or the EVSE-ID; null if the entity no longer exists.</param>
/// <param name="Attribute"><c>identity</c>: a record fits several locations; <c>evse_id</c>: EVSE-IDs that collide.</param>
/// <param name="Values">JSON object with the values per source, as recorded.</param>
public sealed record ConflictView(
    Guid Id,
    string EntityType,
    Guid? EntityId,
    string? EntityLabel,
    string Attribute,
    JsonElement Values,
    DateTimeOffset DetectedAt,
    DateTimeOffset? ResolvedAt,
    string? Resolution);

/// <param name="Open">Number of conflicts not resolved yet.</param>
public sealed record ConflictList(IReadOnlyList<ConflictView> Conflicts, int Open);

public interface IConflictQueries
{
    /// <summary>The newest conflicts first, the open ones or (<paramref name="resolved"/>) the resolved ones.</summary>
    Task<ConflictList> ListAsync(bool resolved, int limit, CancellationToken cancellationToken);
}
