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
/// but never commits. Matching cascade: learned source record, EVSE-ID (exact or normalized), same
/// operator within <see cref="OperatorRadiusMeters"/>, the only location within <see cref="GeoOnlyRadiusMeters"/>,
/// new entity (surrogate without EVSE-ID). The geo steps only consider locations this source does not
/// know yet; several candidates become a <see cref="SourceConflict"/> instead of a merge.
/// </summary>
public sealed class IngestProcessor(AppDbContext db)
{
    public const int OperatorRadiusMeters = 50;
    public const int GeoOnlyRadiusMeters = 25;

    /// <summary>Confidence of a match by operator and distance.</summary>
    public const double GeoOperatorConfidence = 0.9;

    /// <summary>Confidence of a match by distance alone.</summary>
    public const double GeoOnlyConfidence = 0.6;

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
    private Dictionary<string, SourceTier> _tiers = [];

    /// <summary>Records of the message a maintainer declared locations of their own (match decision SEPARATE).</summary>
    private HashSet<string> _separate = [];

    public async Task<IngestResult> ProcessAsync(string sourceKey, IngestBatch batch, CancellationToken cancellationToken)
    {
        _locations = _evses = _statuses = _unknownEvses = _conflicts = _skipped = 0;
        await PrefetchAsync(sourceKey, batch, cancellationToken);
        var readings = new List<StatusReading>();
        foreach (var item in batch.Locations)
        {
            await MergeLocationAsync(sourceKey, item, readings, cancellationToken);
        }

        // One write for all locations of the message (the change tracker keeps the order of dependent rows).
        await db.SaveChangesAsync(cancellationToken);

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

        await InsertObservationsAsync(sourceKey, readings, cancellationToken);
        List<Guid> readingEvses = [.. readings.Select(reading => reading.EvseId).Distinct()];
        await db.EvseStatusCurrent.Where(current => readingEvses.Contains(current.EvseId)).LoadAsync(cancellationToken);
        foreach (var reading in readings)
        {
            await ApplyStatusAsync(sourceKey, reading, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return new IngestResult(_locations, _evses, _statuses, _unknownEvses, _conflicts, _skipped);
    }

    /// <summary>
    /// Loads what the message refers to with a few queries: source records, locations, EVSEs with connectors, attribute
    /// origins, current prices and statuses, operators. Afterwards the lookups of the merge find them in the change
    /// tracker instead of asking the database row by row.
    /// </summary>
    private async Task PrefetchAsync(string sourceKey, IngestBatch batch, CancellationToken cancellationToken)
    {
        var evseSnapshots = batch.Locations.SelectMany(item => item.Location.Evses).ToList();
        List<string> locationKeys = [.. batch.Locations.Select(item => item.Location.ExternalId).Distinct()];
        List<string> evseKeys = [.. evseSnapshots.Select(evse => evse.ExternalId).Concat(batch.Statuses.Select(status => status.ExternalId)).Distinct()];
        List<string> evseIds = [.. evseSnapshots.Select(evse => evse.EvseId)
            .Concat(batch.Statuses.Select(status => status.EvseId))
            .Select(id => EvseId.TryParse(id, out var parsed) ? parsed.Normalized : null)
            .OfType<string>()
            .Distinct()];
        var records = await db.SourceRecords
            .Where(record => record.SourceKey == sourceKey
                && ((record.EntityType == EntityType.LOCATION && locationKeys.Contains(record.ExternalId))
                    || (record.EntityType == EntityType.EVSE && evseKeys.Contains(record.ExternalId))))
            .ToListAsync(cancellationToken);
        List<Guid> locationIds = [.. records.Where(record => record.EntityType == EntityType.LOCATION).Select(record => record.EntityId)];
        List<Guid> recordEvses = [.. records.Where(record => record.EntityType == EntityType.EVSE).Select(record => record.EntityId)];
        await db.Locations.Where(location => locationIds.Contains(location.Id)).LoadAsync(cancellationToken);
        await db.ChargingStations.Where(station => locationIds.Contains(station.LocationId)).LoadAsync(cancellationToken);
        var evses = await db.Evses
            .Include(evse => evse.Connectors)
            .Where(evse => recordEvses.Contains(evse.Id) || evseIds.Contains(evse.EvseIdNorm))
            .ToListAsync(cancellationToken);
        List<Guid> knownEvses = [.. evses.Select(evse => evse.Id)];
        List<Guid> entities = [.. locationIds.Concat(knownEvses)];
        await db.AttributeOrigins.Where(origin => entities.Contains(origin.EntityId)).LoadAsync(cancellationToken);
        await db.EvsePriceCurrent.Where(price => price.SourceKey == sourceKey && knownEvses.Contains(price.EvseId)).LoadAsync(cancellationToken);
        List<string> operatorCodes = [.. batch.Locations
            .Select(item => item.Location.OperatorId?.ToUpperInvariant())
            .OfType<string>()
            .Distinct()];
        await db.Operators.Where(item => item.OperatorId != null && operatorCodes.Contains(item.OperatorId)).LoadAsync(cancellationToken);
        // The tiers decide which source may take an attribute over (docs/03, precedence).
        _tiers = await db.Sources.AsNoTracking().ToDictionaryAsync(source => source.Key, source => source.Tier, cancellationToken);
        _separate = [.. await db.MatchDecisions
            .Where(decision => decision.SourceKey == sourceKey && decision.Decision == MatchDecision.Separate && locationKeys.Contains(decision.ExternalId))
            .Select(decision => decision.ExternalId)
            .ToListAsync(cancellationToken)];
    }

    /// <summary>
    /// Every reading becomes an observation, written with one statement (empty arrays insert nothing). A message that is
    /// processed again must not fail on readings it already stored.
    /// </summary>
    private async Task InsertObservationsAsync(string sourceKey, List<StatusReading> readings, CancellationToken cancellationToken)
    {
        var observations = readings
            .Select(reading => EvseStatusObservation.Create(reading.EvseId, sourceKey, reading.Status, reading.Raw, reading.ObservedAt, reading.SourceReportedAt))
            .ToList();
        Guid[] evses = [.. observations.Select(item => item.EvseId)];
        DateTimeOffset[] times = [.. observations.Select(item => item.Time)];
        string[] statuses = [.. observations.Select(item => item.Status.ToString())];
        string?[] raws = [.. observations.Select(item => item.SourceStatusRaw)];
        DateTimeOffset?[] reported = [.. observations.Select(item => item.SourceReportedAt)];
        int?[] latencies = [.. observations.Select(item => item.LatencyMs)];
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO evse_status_observation (evse_id, time, source_key, status, source_status_raw, source_reported_at, latency_ms)
            SELECT evse_id, time, {sourceKey}, status, raw, reported, latency
            FROM unnest({evses}, {times}, {statuses}, {raws}, {reported}, {latencies}) AS t(evse_id, time, status, raw, reported, latency)
            ON CONFLICT DO NOTHING
            """,
            cancellationToken);
        _statuses += observations.Count;
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
        var method = record?.MatchMethod ?? MatchMethod.CREATED;
        if (record is null || location is null)
        {
            // Matching step 1: an EVSE of the record is already known by its EVSE-ID.
            var known = snapshot.Evses
                .Select(evse => EvseId.TryParse(evse.EvseId, out var id) ? id.Normalized : null)
                .OfType<string>()
                .ToList();
            var knownLocation = db.Evses.Local.Where(evse => known.Contains(evse.EvseIdNorm)).Select(evse => (Guid?)evse.LocationId).FirstOrDefault()
                ?? await db.Evses
                    .Where(evse => known.Contains(evse.EvseIdNorm))
                    .Select(evse => (Guid?)evse.LocationId)
                    .FirstOrDefaultAsync(cancellationToken);
            location = knownLocation is { } id ? await db.Locations.FindAsync([id], cancellationToken) : null;
            method = knownLocation is null ? MatchMethod.CREATED : MatchMethod.EVSE_ID_NORMALIZED;
            var confidence = 1d;
            if (location is null && !_separate.Contains(snapshot.ExternalId))
            {
                // Matching steps 3 and 4: a location of another source close by (unless a maintainer decided otherwise).
                var (match, ambiguous) = await GeoMatchAsync(sourceKey, snapshot, at, cancellationToken);
                if (ambiguous)
                {
                    _skipped++;
                    return;
                }

                if (match is { } geo)
                {
                    location = await db.Locations.FindAsync([geo.Id], cancellationToken);
                    (method, confidence) = (geo.Method, geo.Confidence);
                }
            }

            if (location is null)
            {
                location = Location.Create(snapshot.Coordinates, country, at);
                db.Locations.Add(location);
            }

            if (record is null)
            {
                db.SourceRecords.Add(SourceRecord.Create(sourceKey, snapshot.ExternalId, EntityType.LOCATION, location.Id, method, confidence, at, item.SnapshotId));
            }
            else
            {
                record.Rematch(location.Id, method, confidence, at, item.SnapshotId);
            }
        }
        else
        {
            record.Seen(at, item.SnapshotId);
        }

        // The operator belongs to the country of its EVSE-ID prefix, which may differ from the address (CH*ECU in Austria).
        var operatorCountry = snapshot.OperatorCountry is { } given && IsCountryCode(given) ? given : country;
        var operatorId = await ResolveOperatorAsync(snapshot, operatorCountry, cancellationToken);
        var attributes = new AttributeWriter(db, sourceKey, EntityType.LOCATION, location.Id, at, _tiers);
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
        if (snapshot.Facilities is { } facilities)
        {
            // A source that describes the facilities owns all of them; null means it does not know the value.
            await attributes.SetAsync("green_energy", Text(facilities.GreenEnergy), _ => location.GreenEnergy = facilities.GreenEnergy, cancellationToken);
            await attributes.SetAsync("free_parking", Text(facilities.FreeParking), _ => location.FreeParking = facilities.FreeParking, cancellationToken);
            await attributes.SetAsync("roofed_parking", Text(facilities.RoofedParking), _ => location.RoofedParking = facilities.RoofedParking, cancellationToken);
            await attributes.SetAsync(
                "barrier_free_parking_spaces",
                facilities.BarrierFreeParkingSpaces?.ToString(CultureInfo.InvariantCulture),
                _ => location.BarrierFreeParkingSpaces = facilities.BarrierFreeParkingSpaces,
                cancellationToken);
            await attributes.SetAsync("price_url", facilities.PriceUrl, value => location.PriceUrl = value, cancellationToken);
        }

        Touch(location, attributes.Changed, at);
        _locations++;

        // At a location found by distance or chosen by a maintainer, charge points without EVSE-ID would duplicate those of the other sources.
        var othersHaveEvses = method is MatchMethod.GEO_OPERATOR or MatchMethod.GEO_ONLY or MatchMethod.MANUAL
            && await OtherSourcesHaveEvsesAsync(sourceKey, location.Id, cancellationToken);
        foreach (var evseSnapshot in snapshot.Evses)
        {
            if (othersHaveEvses
                && !EvseId.TryParse(evseSnapshot.EvseId, out _)
                && await db.SourceRecords.FindAsync([sourceKey, evseSnapshot.ExternalId, EntityType.EVSE], cancellationToken) is null)
            {
                continue;
            }

            if (await MergeEvseAsync(sourceKey, location.Id, operatorId, evseSnapshot, item.SnapshotId, at, cancellationToken) is not { } evse)
            {
                continue;
            }

            if (evseSnapshot.Status is { } status)
            {
                readings.Add(new StatusReading(evse.Id, status, evseSnapshot.StatusRaw ?? status.ToString(), at, null));
            }

            if (evseSnapshot.AdHocPrice is { } price)
            {
                await ApplyPriceAsync(evse.Id, sourceKey, price, at, cancellationToken);
            }
        }
    }

    /// <summary>Keeps the current ad-hoc price of the EVSE; a changed price also goes into the history.</summary>
    private async Task ApplyPriceAsync(Guid evseId, string sourceKey, PriceComponents price, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var current = await db.EvsePriceCurrent.FindAsync([evseId, sourceKey, TariffKind.AD_HOC], cancellationToken);
        if (current is null)
        {
            db.EvsePriceCurrent.Add(EvsePriceCurrent.Create(evseId, sourceKey, TariffKind.AD_HOC, price, at));
        }
        else if (!current.Observe(price, at))
        {
            return;
        }

        db.PriceObservations.Add(PriceObservation.Create(evseId, sourceKey, TariffKind.AD_HOC, price, at));
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

        var attributes = new AttributeWriter(db, sourceKey, EntityType.EVSE, evse.Id, at, _tiers);
        await attributes.SetAsync("location_id", locationId.ToString(), _ => evse.LocationId = locationId, cancellationToken);
        await attributes.SetAsync("operator_id", operatorId?.ToString(), _ => evse.OperatorId = operatorId, cancellationToken);
        await attributes.SetAsync(
            "max_power_kw",
            snapshot.MaxPowerKw?.ToString(CultureInfo.InvariantCulture),
            _ => evse.MaxPowerKw = snapshot.MaxPowerKw,
            cancellationToken);
        await attributes.SetAsync("power_type", snapshot.PowerType?.ToString(), _ => evse.PowerType = snapshot.PowerType, cancellationToken);
        await attributes.SetAsync("physical_reference", snapshot.PhysicalReference, value => evse.PhysicalReference = value, cancellationToken);
        // The charging station the source names (E-Control: the station part of a four-part EVSE-ID), one per location
        // and label; without one the EVSE keeps its station.
        if (snapshot.StationExternalId is { } stationRef)
        {
            var station = await FindStationAsync(evse.LocationId, stationRef, cancellationToken);
            await attributes.SetAsync(
                "charging_station",
                stationRef,
                _ => evse.ChargingStationId = (station ??= NewStation(evse.LocationId, stationRef)).Id,
                cancellationToken);
        }

        await attributes.SetAsync(
            "connectors",
            JsonSerializer.Serialize(snapshot.Connectors, Json),
            _ => ApplyConnectors(evse, snapshot.Connectors),
            cancellationToken);
        // Null means the source does not tell; the attribute is then left to the sources that do.
        if (snapshot.AuthMethods is { } methods)
        {
            await attributes.SetAsync("auth_methods", string.Join(',', methods), _ => evse.AuthMethods = methods, cancellationToken);
        }

        if (snapshot.Roaming is { } roaming)
        {
            await attributes.SetAsync("roaming", Text(roaming), _ => evse.Roaming = roaming, cancellationToken);
        }

        Touch(evse, attributes.Changed, at);
        _evses++;
        return evse;
    }

    private async Task<ChargingStation?> FindStationAsync(Guid locationId, string label, CancellationToken cancellationToken) =>
        db.ChargingStations.Local.FirstOrDefault(station => station.LocationId == locationId && station.Label == label)
        ?? await db.ChargingStations.FirstOrDefaultAsync(station => station.LocationId == locationId && station.Label == label, cancellationToken);

    private ChargingStation NewStation(Guid locationId, string label)
    {
        var station = ChargingStation.Create(locationId);
        station.Label = label;
        db.ChargingStations.Add(station);
        return station;
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

    /// <summary>
    /// Matching steps 3 and 4 (docs/03): locations within <see cref="OperatorRadiusMeters"/> that this source does not
    /// know yet; one with the same operator and a similar number of EVSEs, otherwise the only one within
    /// <see cref="GeoOnlyRadiusMeters"/> that has no other operator. Several candidates are recorded as conflict.
    /// </summary>
    private async Task<(GeoMatch? Match, bool Ambiguous)> GeoMatchAsync(
        string sourceKey, ProviderLocationSnapshot snapshot, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var (latitude, longitude) = (snapshot.Coordinates.Latitude, snapshot.Coordinates.Longitude);
        var rows = await db.Database.SqlQuery<GeoCandidate>($"""
            SELECT l.id AS "Id",
                   ST_Distance(l.geom, ST_SetSRID(ST_MakePoint({longitude}, {latitude}), 4326)::geography) AS "Distance",
                   o.operator_id AS "OperatorCode",
                   o.name AS "OperatorName",
                   (SELECT count(*) FROM evse e WHERE e.location_id = l.id AND e.lifecycle = 'ACTIVE')::int AS "Evses"
            FROM location l
            LEFT JOIN operator o ON o.id = l.operator_id
            WHERE l.lifecycle = 'ACTIVE'
              AND ST_DWithin(l.geom, ST_SetSRID(ST_MakePoint({longitude}, {latitude}), 4326)::geography, {OperatorRadiusMeters})
              AND NOT EXISTS (SELECT 1 FROM source_record r
                              WHERE r.entity_type = 'LOCATION' AND r.entity_id = l.id AND r.source_key = {sourceKey})
            ORDER BY 2
            """).ToListAsync(cancellationToken);
        // Locations this message has already given to another record of the source are taken.
        var taken = db.SourceRecords.Local
            .Where(item => item.SourceKey == sourceKey && item.EntityType == EntityType.LOCATION)
            .Select(item => item.EntityId)
            .ToHashSet();
        var candidates = rows.Where(row => !taken.Contains(row.Id)).ToList();
        var sameOperator = candidates.Where(row => SameOperator(snapshot, row) == true && SimilarCount(snapshot.Evses.Count, row.Evses)).ToList();
        var near = candidates.Where(row => row.Distance <= GeoOnlyRadiusMeters && SameOperator(snapshot, row) != false).ToList();
        var (found, method, confidence) = sameOperator.Count > 0
            ? (sameOperator, MatchMethod.GEO_OPERATOR, GeoOperatorConfidence)
            : (near, MatchMethod.GEO_ONLY, GeoOnlyConfidence);
        if (found.Count == 1)
        {
            return (new GeoMatch(found[0].Id, method, confidence), false);
        }

        if (found.Count > 1)
        {
            AmbiguousConflict(sourceKey, snapshot, found, at);
            return (null, true);
        }

        return (null, false);
    }

    /// <summary>True or false if both sides name an operator (code or name), null if one of them does not.</summary>
    private static bool? SameOperator(ProviderLocationSnapshot snapshot, GeoCandidate candidate) =>
        snapshot.OperatorId is { } code && candidate.OperatorCode is { } other
            ? string.Equals(code, other, StringComparison.OrdinalIgnoreCase) || OperatorNames.Same(snapshot.OperatorName, candidate.OperatorName)
            : snapshot.OperatorName is null || candidate.OperatorName is null
                ? null
                : OperatorNames.Same(snapshot.OperatorName, candidate.OperatorName);

    /// <summary>Unknown counts match; otherwise at most twice as many on one side (sources count EVSEs differently).</summary>
    private static bool SimilarCount(int delivered, int known) =>
        delivered == 0 || known == 0 || Math.Min(delivered, known) * 2 >= Math.Max(delivered, known);

    /// <summary>A record that fits several locations; the same open conflict is not recorded twice.</summary>
    private void AmbiguousConflict(string sourceKey, ProviderLocationSnapshot snapshot, List<GeoCandidate> candidates, DateTimeOffset at)
    {
        // The position of the record lets the conflict list show how far each candidate is away.
        var values = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["source"] = sourceKey,
            ["externalId"] = snapshot.ExternalId,
            ["latitude"] = snapshot.Coordinates.Latitude,
            ["longitude"] = snapshot.Coordinates.Longitude,
            ["candidates"] = candidates.Select(candidate => candidate.Id).Order().ToList(),
        });
        if (OpenConflictExists("identity", values))
        {
            return;
        }

        db.SourceConflicts.Add(SourceConflict.Create(EntityType.LOCATION, candidates[0].Id, "identity", values, at));
        _conflicts++;
    }

    /// <summary>Whether the same conflict is open already (in this message or stored); every run of a source repeats it.</summary>
    private bool OpenConflictExists(string attribute, string values) =>
        db.SourceConflicts.Local.Any(conflict => conflict.ResolvedAt == null && conflict.Attribute == attribute && conflict.Values == values)
        || db.SourceConflicts.Any(conflict => conflict.ResolvedAt == null && conflict.Attribute == attribute && conflict.Values == values);

    private Task<bool> OtherSourcesHaveEvsesAsync(string sourceKey, Guid locationId, CancellationToken cancellationToken) =>
        db.Evses.AnyAsync(
            evse => evse.LocationId == locationId
                && evse.Lifecycle == Lifecycle.ACTIVE
                && !db.SourceRecords.Any(record => record.EntityType == EntityType.EVSE && record.EntityId == evse.Id && record.SourceKey == sourceKey),
            cancellationToken);

    private async Task<Guid?> ResolveOperatorAsync(ProviderLocationSnapshot snapshot, string country, CancellationToken cancellationToken)
    {
        if (snapshot.OperatorId is not { Length: 3 } code || !code.All(char.IsAsciiLetterOrDigit))
        {
            return await OperatorByNameAsync(snapshot.OperatorName, country, cancellationToken);
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

    /// <summary>An operator without operator ID, known by its name in the country (sources without EVSE-IDs).</summary>
    private async Task<Guid?> OperatorByNameAsync(string? name, string country, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        name = name.Trim();
        var existing = db.Operators.Local.FirstOrDefault(item => item.Country == country && item.OperatorId == null && item.Name == name)
            ?? await db.Operators.FirstOrDefaultAsync(item => item.Country == country && item.OperatorId == null && item.Name == name, cancellationToken);
        if (existing is null)
        {
            existing = Operator.Create(name, country);
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

    /// <summary>Intervals and the current status change only on a status change; written with the message.</summary>
    private async Task ApplyStatusAsync(string sourceKey, StatusReading reading, CancellationToken cancellationToken)
    {
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
    }

    private void Conflict(Guid evseId, string stored, string delivered, string sourceKey, DateTimeOffset at)
    {
        var values = JsonSerializer.Serialize(new Dictionary<string, string> { ["stored"] = stored, [sourceKey] = delivered });
        if (OpenConflictExists("evse_id", values))
        {
            return;
        }

        db.SourceConflicts.Add(SourceConflict.Create(EntityType.EVSE, evseId, "evse_id", values, at));
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

    private static string? Text(bool? value) => value is { } flag ? (flag ? "true" : "false") : null;

    private static bool IsCountryCode(string value) => value is [>= 'A' and <= 'Z', >= 'A' and <= 'Z'];

    private sealed record StatusReading(Guid EvseId, EvseStatus Status, string Raw, DateTimeOffset ObservedAt, DateTimeOffset? SourceReportedAt);

    private sealed record GeoMatch(Guid Id, MatchMethod Method, double Confidence);

    /// <summary>One row of the geo candidate query; property names are the column aliases.</summary>
    private sealed class GeoCandidate
    {
        public Guid Id { get; set; }

        public double Distance { get; set; }

        public string? OperatorCode { get; set; }

        public string? OperatorName { get; set; }

        public int Evses { get; set; }
    }

    /// <summary>
    /// Writes an attribute if nobody owns it yet, if this source owns it and the value changed, or if this source ranks
    /// above the owner for the attribute (<see cref="AttributePrecedence"/>) and delivers a value; equal ranks keep the
    /// first owner.
    /// </summary>
    private sealed class AttributeWriter(
        AppDbContext db, string sourceKey, EntityType entityType, Guid entityId, DateTimeOffset at, IReadOnlyDictionary<string, SourceTier> tiers)
    {
        public bool Changed { get; private set; }

        public async Task SetAsync(string attribute, string? value, Action<string?> apply, CancellationToken cancellationToken)
        {
            // Finds tracked (also newly added) origins by key without a query; the message prefetched the stored ones.
            var origin = await db.AttributeOrigins.FindAsync([entityType, entityId, attribute], cancellationToken);
            if (origin is null)
            {
                db.AttributeOrigins.Add(AttributeOrigin.Create(entityType, entityId, attribute, sourceKey, value, at));
                apply(value);
                Changed = true;
            }
            else if (origin.SourceKey == sourceKey ? !origin.Matches(value) : value is not null && Ranks(sourceKey) > Ranks(origin.SourceKey))
            {
                origin.Update(sourceKey, value, at);
                apply(value);
                Changed = true;
            }

            int Ranks(string key) => AttributePrecedence.Rank(tiers.TryGetValue(key, out var tier) ? tier : null, attribute);
        }
    }
}
