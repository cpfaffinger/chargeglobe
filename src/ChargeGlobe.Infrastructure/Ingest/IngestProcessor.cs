using System.Globalization;
using System.Text.Json;
using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Infrastructure.Persistence;
using ChargeGlobe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Infrastructure.Ingest;

/// <summary>Counts of one processed message, for the log.</summary>
public sealed record IngestResult(int Locations, int Evses, int Statuses, int UnknownEvses, int Conflicts, int Skipped);

/// <summary>
/// Merges one ingest message into the canonical model (docs/03): identity resolution, attribute
/// origin, source records and status time series. Runs inside the transaction of the queue; it saves,
/// but never commits. Implemented matching steps: EVSE-ID (exact or normalized), learned source
/// record, new entity (surrogate without EVSE-ID). The geo heuristics follow with a second source.
/// </summary>
public sealed class IngestProcessor(AppDbContext db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private int _locations;
    private int _evses;
    private int _statuses;
    private int _unknownEvses;
    private int _conflicts;
    private int _skipped;

    public async Task<IngestResult> ProcessAsync(string sourceKey, IngestBatch batch, CancellationToken cancellationToken)
    {
        _locations = _evses = _statuses = _unknownEvses = _conflicts = _skipped = 0;
        var readings = new List<StatusReading>();
        foreach (var item in batch.Locations)
        {
            await MergeLocationAsync(sourceKey, item, readings, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        foreach (var status in batch.Statuses)
        {
            if (await ResolveEvseAsync(sourceKey, status, cancellationToken) is { } evseId)
            {
                readings.Add(new StatusReading(evseId, status.Status, status.StatusRaw, status.ObservedAt, status.SourceReportedAt));
            }
            else
            {
                _unknownEvses++;
            }
        }

        foreach (var reading in readings)
        {
            await ApplyStatusAsync(sourceKey, reading, cancellationToken);
        }

        return new IngestResult(_locations, _evses, _statuses, _unknownEvses, _conflicts, _skipped);
    }

    private async Task MergeLocationAsync(
        string sourceKey, IngestLocation item, List<StatusReading> readings, CancellationToken cancellationToken)
    {
        var snapshot = item.Location;
        var at = snapshot.ObservedAt;
        if (snapshot.Address?.Country is not { } country || !IsCountryCode(country))
        {
            // The country cannot be derived without guessing (docs/04, rule 2).
            _skipped++;
            return;
        }

        var record = await db.SourceRecords.FindAsync([sourceKey, snapshot.ExternalId, EntityType.LOCATION], cancellationToken);
        var location = record is null ? null : await db.Locations.FindAsync([record.EntityId], cancellationToken);
        if (record is null || location is null)
        {
            // Matching step 1: an EVSE of the record is already known by its EVSE-ID.
            var known = snapshot.Evses
                .Select(evse => EvseId.TryParse(evse.EvseId, out var id) ? id.Normalized : null)
                .OfType<string>()
                .ToList();
            var knownLocation = await db.Evses
                .Where(evse => known.Contains(evse.EvseIdNorm))
                .Select(evse => (Guid?)evse.LocationId)
                .FirstOrDefaultAsync(cancellationToken);
            location = knownLocation is { } id ? await db.Locations.FindAsync([id], cancellationToken) : null;
            if (location is null)
            {
                location = Location.Create(snapshot.Coordinates, country, at);
                db.Locations.Add(location);
            }

            var method = knownLocation is null ? MatchMethod.CREATED : MatchMethod.EVSE_ID_NORMALIZED;
            if (record is null)
            {
                db.SourceRecords.Add(SourceRecord.Create(sourceKey, snapshot.ExternalId, EntityType.LOCATION, location.Id, method, 1, at, item.SnapshotId));
            }
            else
            {
                record.Rematch(location.Id, method, 1, at, item.SnapshotId);
            }
        }
        else
        {
            record.Seen(at, item.SnapshotId);
        }

        var operatorId = await ResolveOperatorAsync(snapshot, country, cancellationToken);
        var attributes = new AttributeWriter(db, sourceKey, EntityType.LOCATION, location.Id, at);
        await attributes.SetAsync("name", snapshot.Name, value => location.Name = value, cancellationToken);
        await attributes.SetAsync("address", snapshot.Address.Street, value => location.Address = value, cancellationToken);
        await attributes.SetAsync("postal_code", snapshot.Address.PostalCode, value => location.PostalCode = value, cancellationToken);
        await attributes.SetAsync("city", snapshot.Address.City, value => location.City = value, cancellationToken);
        await attributes.SetAsync(
            "geom",
            string.Create(CultureInfo.InvariantCulture, $"{snapshot.Coordinates.Latitude:0.0000000},{snapshot.Coordinates.Longitude:0.0000000}"),
            _ => location.Coordinates = snapshot.Coordinates,
            cancellationToken);
        await attributes.SetAsync("operator_id", operatorId?.ToString(), _ => location.OperatorId = operatorId, cancellationToken);
        await attributes.SetAsync("access", snapshot.Access?.ToString(), _ => location.Access = snapshot.Access ?? AccessType.PUBLIC, cancellationToken);
        await attributes.SetAsync("opening_times", OpeningTimesJson(snapshot.OpeningTimes), value => location.OpeningTimes = value, cancellationToken);
        Touch(location, attributes.Changed, at);
        _locations++;

        foreach (var evseSnapshot in snapshot.Evses)
        {
            if (await MergeEvseAsync(sourceKey, location.Id, operatorId, evseSnapshot, item.SnapshotId, at, cancellationToken) is { } evse
                && evseSnapshot.Status is { } status)
            {
                readings.Add(new StatusReading(evse.Id, status, evseSnapshot.StatusRaw ?? status.ToString(), at, null));
            }
        }
    }

    private async Task<Evse?> MergeEvseAsync(
        string sourceKey,
        Guid locationId,
        Guid? operatorId,
        ProviderEvseSnapshot snapshot,
        long? snapshotId,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var hasEvseId = EvseId.TryParse(snapshot.EvseId, out var evseId);
        var record = await db.SourceRecords.FindAsync([sourceKey, snapshot.ExternalId, EntityType.EVSE], cancellationToken);
        var evse = record is null ? null : await FindEvseAsync(item => item.Id == record.EntityId, cancellationToken);
        if (evse is null)
        {
            var method = MatchMethod.CREATED;
            if (hasEvseId)
            {
                evse = await FindEvseAsync(item => item.EvseIdNorm == evseId!.Normalized, cancellationToken);
                if (evse is not null && evse.EvseIdDisplay != evseId!.Display)
                {
                    // Same normalized form, different separators: possibly a collision (docs/03), not merged.
                    Conflict(evse.Id, evse.EvseIdDisplay, evseId.Display, sourceKey, at);
                    return null;
                }

                method = evse is null ? MatchMethod.CREATED : MatchMethod.EVSE_ID_EXACT;
            }

            if (evse is null)
            {
                evse = hasEvseId ? Evse.WithEvseId(evseId!, locationId, at) : Evse.WithSurrogate(locationId, at);
                db.Evses.Add(evse);
            }

            if (record is null)
            {
                db.SourceRecords.Add(SourceRecord.Create(sourceKey, snapshot.ExternalId, EntityType.EVSE, evse.Id, method, 1, at, snapshotId));
            }
            else
            {
                record.Rematch(evse.Id, method, 1, at, snapshotId);
            }
        }
        else
        {
            record!.Seen(at, snapshotId);
            if (evse.IdentityKind == IdentityKind.SURROGATE && hasEvseId)
            {
                if (await FindEvseAsync(item => item.EvseIdNorm == evseId!.Normalized, cancellationToken) is { } holder)
                {
                    Conflict(evse.Id, holder.EvseIdDisplay, evseId!.Display, sourceKey, at);
                }
                else
                {
                    evse.AssignEvseId(evseId!);
                }
            }
        }

        var attributes = new AttributeWriter(db, sourceKey, EntityType.EVSE, evse.Id, at);
        await attributes.SetAsync("location_id", locationId.ToString(), _ => evse.LocationId = locationId, cancellationToken);
        await attributes.SetAsync("operator_id", operatorId?.ToString(), _ => evse.OperatorId = operatorId, cancellationToken);
        await attributes.SetAsync(
            "max_power_kw",
            snapshot.MaxPowerKw?.ToString(CultureInfo.InvariantCulture),
            _ => evse.MaxPowerKw = snapshot.MaxPowerKw,
            cancellationToken);
        await attributes.SetAsync("power_type", snapshot.PowerType?.ToString(), _ => evse.PowerType = snapshot.PowerType, cancellationToken);
        await attributes.SetAsync("physical_reference", snapshot.PhysicalReference, value => evse.PhysicalReference = value, cancellationToken);
        await attributes.SetAsync(
            "connectors",
            JsonSerializer.Serialize(snapshot.Connectors, Json),
            _ => ApplyConnectors(evse, snapshot.Connectors),
            cancellationToken);
        Touch(evse, attributes.Changed, at);
        _evses++;
        return evse;
    }

    private async Task<Evse?> FindEvseAsync(System.Linq.Expressions.Expression<Func<Evse, bool>> predicate, CancellationToken cancellationToken) =>
        db.Evses.Local.AsQueryable().FirstOrDefault(predicate)
        ?? await db.Evses.Include(evse => evse.Connectors).FirstOrDefaultAsync(predicate, cancellationToken);

    private static void ApplyConnectors(Evse evse, IReadOnlyList<ProviderConnectorSnapshot> connectors)
    {
        for (var index = 0; index < connectors.Count; index++)
        {
            var source = connectors[index];
            var connector = index < evse.Connectors.Count
                ? evse.Connectors[index]
                : evse.AddConnector(source.Standard, source.Format, source.PowerType);
            connector.Standard = source.Standard;
            connector.Format = source.Format;
            connector.PowerType = source.PowerType;
            connector.MaxPowerKw = source.MaxPowerKw;
            connector.MaxVoltage = source.MaxVoltage;
            connector.MaxAmperage = source.MaxAmperage;
        }

        evse.RemoveConnectorsFrom(connectors.Count + 1);
    }

    private async Task<Guid?> ResolveOperatorAsync(ProviderLocationSnapshot snapshot, string country, CancellationToken cancellationToken)
    {
        if (snapshot.OperatorId is not { Length: 3 } code || !code.All(char.IsAsciiLetterOrDigit))
        {
            return null;
        }

        code = code.ToUpperInvariant();
        var existing = db.Operators.Local.FirstOrDefault(item => item.Country == country && item.OperatorId == code)
            ?? await db.Operators.FirstOrDefaultAsync(item => item.Country == country && item.OperatorId == code, cancellationToken);
        if (existing is null)
        {
            existing = Operator.Create(string.IsNullOrWhiteSpace(snapshot.OperatorName) ? code : snapshot.OperatorName, country, code);
            db.Operators.Add(existing);
        }

        return existing.Id;
    }

    private async Task<Guid?> ResolveEvseAsync(string sourceKey, ProviderEvseStatus status, CancellationToken cancellationToken)
    {
        if (status.CanonicalEvseId is { } canonical)
        {
            return canonical;
        }

        if (await db.SourceRecords.FindAsync([sourceKey, status.ExternalId, EntityType.EVSE], cancellationToken) is { } record)
        {
            return record.EntityId;
        }

        return EvseId.TryParse(status.EvseId, out var evseId)
            ? await db.Evses.Where(evse => evse.EvseIdNorm == evseId.Normalized).Select(evse => (Guid?)evse.Id).FirstOrDefaultAsync(cancellationToken)
            : null;
    }

    /// <summary>Every reading becomes an observation; intervals and the current status change only on a status change.</summary>
    private async Task ApplyStatusAsync(string sourceKey, StatusReading reading, CancellationToken cancellationToken)
    {
        var observation = EvseStatusObservation.Create(
            reading.EvseId, sourceKey, reading.Status, reading.Raw, reading.ObservedAt, reading.SourceReportedAt);
        // A message that is processed again must not fail on readings it already stored.
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO evse_status_observation (evse_id, time, source_key, status, source_status_raw, source_reported_at, latency_ms)
            VALUES ({observation.EvseId}, {observation.Time}, {observation.SourceKey}, {observation.Status.ToString()},
                    {observation.SourceStatusRaw}, {observation.SourceReportedAt}, {observation.LatencyMs})
            ON CONFLICT DO NOTHING
            """,
            cancellationToken);
        _statuses++;

        var current = await db.EvseStatusCurrent.FindAsync([reading.EvseId], cancellationToken);
        if (current is null)
        {
            db.EvseStatusCurrent.Add(EvseStatusCurrent.Create(reading.EvseId, reading.Status, sourceKey, reading.ObservedAt));
            db.EvseStatusIntervals.Add(EvseStatusInterval.Open(reading.EvseId, reading.Status, sourceKey, reading.ObservedAt));
        }
        else if (reading.ObservedAt > current.ObservedAt && reading.Status != current.Status)
        {
            var open = await db.EvseStatusIntervals.SingleOrDefaultAsync(
                interval => interval.EvseId == reading.EvseId && interval.ValidTo == null, cancellationToken);
            open?.Close(reading.ObservedAt);
            // The closed interval must be stored before the new open one (one open interval per EVSE).
            await db.SaveChangesAsync(cancellationToken);
            db.EvseStatusIntervals.Add(EvseStatusInterval.Open(reading.EvseId, reading.Status, sourceKey, reading.ObservedAt));
            current.Observe(reading.Status, sourceKey, reading.ObservedAt);
        }
        else
        {
            current.Observe(reading.Status, sourceKey, reading.ObservedAt);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private void Conflict(Guid evseId, string stored, string delivered, string sourceKey, DateTimeOffset at)
    {
        db.SourceConflicts.Add(SourceConflict.Create(
            EntityType.EVSE,
            evseId,
            "evse_id",
            JsonSerializer.Serialize(new Dictionary<string, string> { ["stored"] = stored, [sourceKey] = delivered }),
            at));
        _conflicts++;
    }

    private static void Touch(TrackedEntity entity, bool changed, DateTimeOffset at)
    {
        if (changed)
        {
            entity.Seen(at);
        }
        else
        {
            entity.Confirmed(at);
        }
    }

    /// <summary>OCPI <c>Hours</c> form; the text of a source without structured hours goes to <c>description</c>.</summary>
    private static string? OpeningTimesJson(ProviderOpeningTimes? times) =>
        times is null
            ? null
            : JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["twentyfourseven"] = times.TwentyFourSeven,
                ["description"] = times.Description,
            });

    private static bool IsCountryCode(string value) => value is [>= 'A' and <= 'Z', >= 'A' and <= 'Z'];

    private sealed record StatusReading(Guid EvseId, EvseStatus Status, string Raw, DateTimeOffset ObservedAt, DateTimeOffset? SourceReportedAt);

    /// <summary>
    /// Writes an attribute only if this source owns it (or nobody does yet) and its value changed. Until
    /// the precedence table of docs/03 is configurable, the first source to set an attribute owns it.
    /// </summary>
    private sealed class AttributeWriter(AppDbContext db, string sourceKey, EntityType entityType, Guid entityId, DateTimeOffset at)
    {
        public bool Changed { get; private set; }

        public async Task SetAsync(string attribute, string? value, Action<string?> apply, CancellationToken cancellationToken)
        {
            var origin = db.AttributeOrigins.Local.FirstOrDefault(item =>
                    item.EntityType == entityType && item.EntityId == entityId && item.Attribute == attribute)
                ?? await db.AttributeOrigins.FindAsync([entityType, entityId, attribute], cancellationToken);
            if (origin is null)
            {
                db.AttributeOrigins.Add(AttributeOrigin.Create(entityType, entityId, attribute, sourceKey, value, at));
                apply(value);
                Changed = true;
            }
            else if (origin.SourceKey == sourceKey && !origin.Matches(value))
            {
                origin.Update(sourceKey, value, at);
                apply(value);
                Changed = true;
            }
        }
    }
}
