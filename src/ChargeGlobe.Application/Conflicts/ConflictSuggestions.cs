using System.Globalization;
using System.Text.Json;

namespace ChargeGlobe.Application.Conflicts;

/// <summary>
/// What a maintainer would most likely decide (docs/03, "Vorschläge"). The rules only use what the conflict shows:
/// the record of the source, the candidates with operator, distance and charge points, and for <c>evse_id</c> the
/// location of the record. They never reach 1: a suggestion is a hint, applying it is a decision.
/// </summary>
public static class ConflictSuggestions
{
    public const string Merge = "merge";
    public const string Keep = "keep";
    public const string Same = "same";

    /// <summary>Same operator and the same number of charge points: as safe as the heuristics get.</summary>
    public const double ExactConfidence = 0.8;

    /// <summary>The nearest candidate of the same operator, or the nearest of candidates that all share an operator.</summary>
    public const double NearestConfidence = 0.6;

    /// <summary>Same EVSE-ID in another notation at the location of the record.</summary>
    public const double SameLocationConfidence = 0.95;

    /// <summary>Same EVSE-ID in another notation, but at another location.</summary>
    public const double OtherLocationConfidence = 0.6;

    /// <summary>
    /// <c>identity</c>: with the record known, the only candidate of its operator with the same number of charge points
    /// (<see cref="ExactConfidence"/>), else the nearest candidate of its operator (<see cref="NearestConfidence"/>);
    /// without the record, the nearest candidate if all candidates share one operator (<see cref="NearestConfidence"/>).
    /// Null if no candidate matches the operator, or none is left.
    /// </summary>
    public static ConflictSuggestion? ForIdentity(ConflictRecord? record, IReadOnlyList<ConflictCandidate> candidates)
    {
        var same = candidates.Where(candidate => candidate.SameOperator == true && candidate.Label is not null).ToList();
        if (same.Count == 0)
        {
            return null;
        }

        if (record is null)
        {
            if (same.Count < candidates.Count)
            {
                return null;
            }

            var nearest = same[0];
            return new ConflictSuggestion(
                Merge,
                nearest.LocationId,
                NearestConfidence,
                $"All {candidates.Count} candidates belong to the operator {nearest.OperatorName ?? nearest.OperatorId}; the record of the source is not recorded yet, so the nearest ({Meters(nearest)}) is suggested.");
        }

        var count = record.Evses.Count;
        var exact = count > 0 ? same.Where(candidate => candidate.Evses == count).ToList() : [];
        if (exact.Count == 1)
        {
            return new ConflictSuggestion(
                Merge,
                exact[0].LocationId,
                ExactConfidence,
                $"The only candidate of the operator {record.OperatorName ?? record.OperatorId} with the same number of charge points ({count}), {Meters(exact[0])} away.");
        }

        var first = same[0];
        return new ConflictSuggestion(
            Merge,
            first.LocationId,
            NearestConfidence,
            $"The nearest of {same.Count} candidates of the operator {record.OperatorName ?? record.OperatorId}, {Meters(first)} away; the record has {count} charge points, the candidate {first.Evses}.");
    }

    /// <summary>
    /// <c>evse_id</c>: the two notations name the same EVSE. <see cref="Same"/> with <see cref="SameLocationConfidence"/>
    /// if the record of the source sits at the location of the stored EVSE, <see cref="OtherLocationConfidence"/> if at
    /// another one. Null for a conflict without the location of the record or whose EVSE is gone.
    /// </summary>
    public static ConflictSuggestion? ForEvseId(JsonElement values, Guid? storedLocationId)
    {
        if (storedLocationId is not { } stored
            || !values.TryGetProperty("locationId", out var element)
            || !element.TryGetGuid(out var locationId))
        {
            return null;
        }

        return locationId == stored
            ? new ConflictSuggestion(Same, null, SameLocationConfidence, "The same EVSE-ID in another notation, delivered at the location of the stored EVSE.")
            : new ConflictSuggestion(Same, null, OtherLocationConfidence, "The same EVSE-ID in another notation, but the record of the source belongs to another location.");
    }

    private static string Meters(ConflictCandidate candidate) =>
        candidate.DistanceMeters is { } meters ? string.Create(CultureInfo.InvariantCulture, $"{meters:0} m") : "an unknown distance";
}
