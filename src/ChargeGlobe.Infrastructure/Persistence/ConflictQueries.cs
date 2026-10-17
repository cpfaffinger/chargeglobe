using System.Globalization;
using System.Text.Json;
using ChargeGlobe.Application.Conflicts;
using ChargeGlobe.Application.Identity;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Infrastructure.Identity;
using ChargeGlobe.Infrastructure.Ingest;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Infrastructure.Persistence;

/// <summary>
/// Conflicts of <c>source_conflict</c> with a label of the location or EVSE they concern, for <c>identity</c> the record
/// of the source and the candidate locations with operator, distance, charge points and sources, and the suggestion of
/// <see cref="ConflictSuggestions"/>.
/// </summary>
public sealed class ConflictQueries(AppDbContext db) : IConflictQueries
{
    /// <summary>Conflicts loaded at once while looking for the ones with a suggestion.</summary>
    public const int BatchSize = 200;

    /// <summary>EVSE-IDs listed per candidate at most; a location with more is a park, the count tells the rest.</summary>
    public const int MaxEvseIds = 50;

    public async Task<ConflictList> ListAsync(ConflictFilter filter, CancellationToken cancellationToken)
    {
        var page = new List<ConflictView>();
        var after = filter.After;
        var wanted = filter.Limit + 1;
        while (page.Count < wanted)
        {
            var batchSize = filter.SuggestedOnly ? BatchSize : wanted - page.Count;
            var batch = await Query(filter with { After = after }).Take(batchSize).ToListAsync(cancellationToken);
            foreach (var view in await ViewsAsync(batch, cancellationToken))
            {
                if (!filter.SuggestedOnly || view.Suggestion is not null)
                {
                    page.Add(view);
                }

                if (page.Count == wanted)
                {
                    break;
                }
            }

            if (batch.Count < batchSize)
            {
                break;
            }

            after = new ConflictCursor(batch[^1].DetectedAt, batch[^1].Id);
        }

        var open = await db.SourceConflicts.CountAsync(conflict => conflict.ResolvedAt == null, cancellationToken);
        var conflicts = page.Take(filter.Limit).ToList();
        var next = page.Count > filter.Limit ? new ConflictCursor(conflicts[^1].DetectedAt, conflicts[^1].Id) : null;
        return new ConflictList(conflicts, open, next);
    }

    public async Task<ConflictView?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.SourceConflicts.AsNoTracking().FirstOrDefaultAsync(conflict => conflict.Id == id, cancellationToken) is { } conflict
            ? (await ViewsAsync([conflict], cancellationToken))[0]
            : null;

    /// <summary>The ids of the candidates of an <c>identity</c> conflict; empty for other conflicts.</summary>
    public static IReadOnlyList<Guid> Candidates(JsonElement values) =>
        values.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array
            ? [.. candidates.EnumerateArray().Select(candidate => candidate.GetGuid())]
            : [];

    /// <summary>The source and the external id of the record that raised the conflict; null for conflicts recorded without them.</summary>
    public static (string Source, string ExternalId)? RecordKey(JsonElement values) =>
        values.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String
            && values.TryGetProperty("externalId", out var externalId) && externalId.ValueKind == JsonValueKind.String
            ? (source.GetString()!, externalId.GetString()!)
            : null;

    /// <summary>Newest first; the cursor continues after a conflict of the previous page.</summary>
    private IQueryable<SourceConflict> Query(ConflictFilter filter)
    {
        var query = filter.After is { } after
            ? db.SourceConflicts.FromSql($"SELECT * FROM source_conflict WHERE (detected_at, id) < ({after.DetectedAt}, {after.Id})")
            : db.SourceConflicts;
        query = query.AsNoTracking().Where(conflict => (conflict.ResolvedAt != null) == filter.Resolved);
        if (filter.Attribute is { } attribute)
        {
            query = query.Where(conflict => conflict.Attribute == attribute);
        }

        if (filter.Source is { } source)
        {
            var key = JsonSerializer.Serialize(new Dictionary<string, string> { ["source"] = source });
            query = query.Where(conflict => EF.Functions.JsonContains(conflict.Values, key));
        }

        return query.OrderByDescending(conflict => conflict.DetectedAt).ThenByDescending(conflict => conflict.Id);
    }

    private async Task<IReadOnlyList<ConflictView>> ViewsAsync(List<SourceConflict> conflicts, CancellationToken cancellationToken)
    {
        var values = conflicts.ToDictionary(conflict => conflict.Id, conflict => JsonDocument.Parse(conflict.Values).RootElement.Clone());
        List<Guid> ids = [.. conflicts.Select(conflict => conflict.EntityId).OfType<Guid>().Concat(values.Values.SelectMany(Candidates)).Distinct()];
        var locations = await (
            from location in db.Locations.AsNoTracking()
            join candidate in db.Operators.AsNoTracking() on location.OperatorId equals candidate.Id into operators
            from @operator in operators.DefaultIfEmpty()
            where ids.Contains(location.Id)
            select new { Location = location, Operator = @operator })
            .ToDictionaryAsync(item => item.Location.Id, cancellationToken);
        var evses = await db.Evses.AsNoTracking()
            .Where(evse => ids.Contains(evse.Id) || (evse.Lifecycle == Lifecycle.ACTIVE && ids.Contains(evse.LocationId)))
            .Select(evse => new { evse.Id, evse.LocationId, evse.EvseIdDisplay, evse.IdentityKind, evse.Lifecycle })
            .ToListAsync(cancellationToken);
        var byId = evses.ToDictionary(evse => evse.Id);
        var byLocation = evses.Where(evse => evse.Lifecycle == Lifecycle.ACTIVE).ToLookup(evse => evse.LocationId);
        var sources = (await db.SourceRecords.AsNoTracking()
            .Where(record => record.EntityType == EntityType.LOCATION && ids.Contains(record.EntityId))
            .Select(record => new { record.EntityId, record.SourceKey })
            .Distinct()
            .ToListAsync(cancellationToken))
            .ToLookup(record => record.EntityId, record => record.SourceKey);

        return [.. conflicts.Select(conflict =>
        {
            var value = values[conflict.Id];
            var record = conflict.Attribute == "identity" ? Record(value) : null;
            var candidates = Candidates(value)
                .Select(id => locations.TryGetValue(id, out var found)
                    ? (Id: id, Location: (Location?)found.Location, Operator: (Operator?)found.Operator)
                    : (Id: id, Location: null, Operator: null))
                .Select(item => new ConflictCandidate(
                    item.Id,
                    Label(item.Location),
                    Position(value) is { } from && item.Location is { } location ? Math.Round(Meters(from, location.Coordinates), 1) : null,
                    item.Operator?.OperatorId,
                    item.Operator?.Name,
                    null,
                    byLocation[item.Id].Count(),
                    byLocation[item.Id].Count(evse => evse.IdentityKind == IdentityKind.SURROGATE),
                    [.. byLocation[item.Id].Select(evse => evse.EvseIdDisplay).Order(StringComparer.Ordinal).Take(MaxEvseIds)],
                    [.. sources[item.Id].Order(StringComparer.Ordinal)]))
                .OrderBy(candidate => candidate.DistanceMeters ?? double.MaxValue)
                .ToList();
            candidates = WithOperatorMatch(record, candidates);
            var stored = conflict.EntityId is { } id && byId.TryGetValue(id, out var evse) ? evse : null;
            return new ConflictView(
                conflict.Id,
                conflict.EntityType.ToString(),
                conflict.EntityId,
                conflict.EntityId is not { } entityId ? null
                    : conflict.EntityType == EntityType.EVSE ? stored?.EvseIdDisplay
                    : Label(locations.GetValueOrDefault(entityId)?.Location),
                conflict.Attribute,
                value,
                conflict.DetectedAt,
                conflict.ResolvedAt,
                conflict.Resolution,
                candidates,
                record,
                conflict.ResolvedAt is not null ? null
                    : conflict.Attribute == "identity" ? ConflictSuggestions.ForIdentity(record, candidates)
                    : conflict.Attribute == "evse_id" ? ConflictSuggestions.ForEvseId(value, stored?.Lifecycle == Lifecycle.ACTIVE ? (Guid?)stored.LocationId : null)
                    : null);
        })];
    }

    /// <summary>
    /// Whether each candidate belongs to the operator of the record (by code or name, as the matching does), or
    /// without the record to the operator of the nearest candidate.
    /// </summary>
    private static List<ConflictCandidate> WithOperatorMatch(ConflictRecord? record, List<ConflictCandidate> candidates)
    {
        var (code, name) = record is not null
            ? (record.OperatorId, record.OperatorName)
            : candidates.Count > 0 ? (candidates[0].OperatorId, candidates[0].OperatorName) : (null, null);
        return [.. candidates.Select(candidate => candidate with { SameOperator = SameOperator(code, name, candidate.OperatorId, candidate.OperatorName) })];
    }

    /// <summary>True or false if both sides name an operator (code or name), null if one of them does not.</summary>
    private static bool? SameOperator(string? code, string? name, string? otherCode, string? otherName) =>
        code is not null && otherCode is not null
            ? string.Equals(code, otherCode, StringComparison.OrdinalIgnoreCase) || OperatorNames.Same(name, otherName)
            : name is null || otherName is null
                ? null
                : OperatorNames.Same(name, otherName);

    private static ConflictRecord? Record(JsonElement values)
    {
        if (RecordKey(values) is not { } key || !values.TryGetProperty("record", out var record) || record.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var position = Position(values);
        var evses = record.TryGetProperty("evses", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(evse => new ConflictRecordEvse(
                Text(evse, "evseId"),
                evse.TryGetProperty("maxPowerKw", out var power) && power.ValueKind == JsonValueKind.Number ? power.GetDecimal() : null)).ToList()
            : [];
        return new ConflictRecord(
            key.Source,
            key.ExternalId,
            Text(record, "name"),
            Text(record, "address"),
            Text(record, "postalCode"),
            Text(record, "city"),
            Text(record, "operatorId"),
            Text(record, "operatorName"),
            position?.Latitude,
            position?.Longitude,
            evses);
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static GeoPoint? Position(JsonElement values) =>
        values.TryGetProperty("latitude", out var latitude) && values.TryGetProperty("longitude", out var longitude)
            ? new GeoPoint(latitude.GetDouble(), longitude.GetDouble())
            : null;

    private static string? Label(Location? location) =>
        location is null ? null : string.Join(", ", new[] { location.Name, location.Address, location.City }.Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>Great-circle distance; a few metres between two positions need no projection.</summary>
    private static double Meters(GeoPoint a, GeoPoint b)
    {
        const double EarthRadius = 6_371_000;
        var (lat1, lat2) = (a.Latitude * Math.PI / 180, b.Latitude * Math.PI / 180);
        var (dLat, dLon) = (lat2 - lat1, (b.Longitude - a.Longitude) * Math.PI / 180);
        var h = Math.Pow(Math.Sin(dLat / 2), 2) + (Math.Cos(lat1) * Math.Cos(lat2) * Math.Pow(Math.Sin(dLon / 2), 2));
        return 2 * EarthRadius * Math.Asin(Math.Sqrt(h));
    }
}

/// <summary>Decisions on conflicts (<see cref="IConflictResolution"/>), with the source record, the match decision and the audit entry.</summary>
public sealed class ConflictResolution(AppDbContext db, IConflictQueries queries, AuditLog audit, TimeProvider timeProvider) : IConflictResolution
{
    public async Task<ResolveResult> ResolveAsync(
        Guid id, ConflictAction action, Guid? targetId, string? note, Actor actor, CancellationToken cancellationToken)
    {
        var conflict = await db.SourceConflicts.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (conflict is null)
        {
            return new ResolveResult(ResolveOutcome.NotFound, null);
        }

        if (conflict.ResolvedAt is not null)
        {
            return new ResolveResult(ResolveOutcome.AlreadyResolved, await queries.FindAsync(id, cancellationToken));
        }

        var now = timeProvider.GetUtcNow();
        var values = JsonDocument.Parse(conflict.Values).RootElement;
        var key = ConflictQueries.RecordKey(values);
        string resolution;
        switch ((conflict.Attribute, action))
        {
            case ("identity", ConflictAction.Merge) when key is { } merged:
                if (targetId is not { } target
                    || !ConflictQueries.Candidates(values).Contains(target)
                    || !await db.Locations.AnyAsync(location => location.Id == target && location.Lifecycle == Lifecycle.ACTIVE, cancellationToken))
                {
                    return new ResolveResult(ResolveOutcome.UnknownTarget, null);
                }

                await MapRecordAsync(merged, EntityType.LOCATION, target, now, cancellationToken);
                await DecideAsync(merged, MatchDecision.Merge, target, conflict.Id, now, actor, cancellationToken);
                resolution = $"Merged into location {target}.";
                break;
            case ("identity", ConflictAction.Separate) when key is { } separated:
                await DecideAsync(separated, MatchDecision.Separate, null, conflict.Id, now, actor, cancellationToken);
                resolution = "Kept as a location of its own.";
                break;
            case ("evse_id", ConflictAction.Keep):
                if (key is { } kept)
                {
                    await DecideAsync(kept, MatchDecision.Keep, null, conflict.Id, now, actor, cancellationToken);
                }

                resolution = "Kept as stored.";
                break;
            case ("evse_id", ConflictAction.Same):
                if (key is not { } same
                    || conflict.EntityId is not { } evseId
                    || await db.Evses.Where(evse => evse.Id == evseId && evse.Lifecycle == Lifecycle.ACTIVE).Select(evse => evse.EvseIdDisplay).FirstOrDefaultAsync(cancellationToken) is not { } display)
                {
                    return new ResolveResult(ResolveOutcome.ActionNotAllowed, null);
                }

                await MapRecordAsync(same, EntityType.EVSE, evseId, now, cancellationToken);
                resolution = $"Mapped to EVSE {display}.";
                break;
            default:
                return new ResolveResult(ResolveOutcome.ActionNotAllowed, null);
        }

        conflict.Resolve(string.IsNullOrWhiteSpace(note) ? resolution : $"{resolution} {note.Trim()}", now);
        audit.Record("conflict.resolve", $"conflict:{id}", actor);
        await db.SaveChangesAsync(cancellationToken);
        return new ResolveResult(ResolveOutcome.Resolved, await queries.FindAsync(id, cancellationToken));
    }

    public async Task<IReadOnlyList<ResolvedItem>> ApplySuggestionsAsync(ApplySuggestions request, Actor actor, CancellationToken cancellationToken)
    {
        var items = new List<ResolvedItem>();
        ConflictCursor? after = null;
        while (items.Count < request.Limit)
        {
            var page = await queries.ListAsync(
                new ConflictFilter(false, request.Attribute, request.Source, true, Math.Min(ConflictQueries.BatchSize, request.Limit - items.Count), after),
                cancellationToken);
            foreach (var view in page.Conflicts)
            {
                var suggestion = view.Suggestion!;
                if (suggestion.Confidence < request.MinConfidence)
                {
                    continue;
                }

                var note = string.Create(CultureInfo.InvariantCulture, $"Suggestion ({suggestion.Confidence:0.00}): {suggestion.Reason}");
                var result = await ResolveAsync(
                    view.Id,
                    ConflictActions.Parse(suggestion.Action)!.Value,
                    suggestion.TargetId,
                    string.IsNullOrWhiteSpace(request.Note) ? note : $"{note} {request.Note.Trim()}",
                    actor,
                    cancellationToken);
                items.Add(new ResolvedItem(view.Id, result.Outcome, result.Conflict));
                if (items.Count == request.Limit)
                {
                    break;
                }
            }

            if (page.Next is null)
            {
                break;
            }

            after = page.Next;
        }

        return items;
    }

    /// <summary>The record of the source belongs to the entity from now on (match method MANUAL).</summary>
    private async Task MapRecordAsync((string Source, string ExternalId) key, EntityType type, Guid entityId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await db.SourceRecords.FindAsync([key.Source, key.ExternalId, type], cancellationToken) is { } record)
        {
            record.Rematch(entityId, MatchMethod.MANUAL, 1, now, null);
        }
        else
        {
            db.SourceRecords.Add(SourceRecord.Create(key.Source, key.ExternalId, type, entityId, MatchMethod.MANUAL, 1, now));
        }
    }

    /// <summary>Remembers the decision for the record, so that the next run of the source follows it (docs/03).</summary>
    private async Task DecideAsync(
        (string Source, string ExternalId) key, string decision, Guid? locationId, Guid conflictId, DateTimeOffset at, Actor actor, CancellationToken cancellationToken)
    {
        var stored = await db.MatchDecisions.FindAsync([key.Source, key.ExternalId], cancellationToken);
        if (stored is null)
        {
            stored = new MatchDecision { SourceKey = key.Source, ExternalId = key.ExternalId, Decision = decision };
            db.MatchDecisions.Add(stored);
        }

        stored.Decision = decision;
        stored.LocationId = locationId;
        stored.ConflictId = conflictId;
        stored.DecidedAt = at;
        stored.DecidedBy = actor.UserId;
    }
}
