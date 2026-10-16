using System.Text.Json;
using ChargeGlobe.Application.Evses;
using ChargeGlobe.Application.Locations;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Provenance;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ChargeGlobe.Infrastructure.Persistence;

/// <summary>
/// EVSE detail read model: master data with the current status, status intervals and the hourly occupancy from the
/// continuous aggregate <c>evse_occupancy_hourly</c>.
/// </summary>
public sealed class EvseQueries(AppDbContext db, IAppConfig config, TimeProvider timeProvider) : IEvseQueries
{
    /// <summary>Postgres error "invalid_parameter_value", raised for an unknown time zone.</summary>
    private const string InvalidParameterValue = "22023";

    public async Task<EvseView?> FindAsync(string evseId, CancellationToken cancellationToken)
    {
        var evses = db.Evses.AsNoTracking().Include(item => item.Connectors);
        // Same normalization as EvseId (separators *, - and blanks), but also for surrogates (XX*CG*E…).
        var normalized = string.Concat(evseId.ToUpperInvariant().Where(character => character is not ('*' or '-') && !char.IsWhiteSpace(character)));
        var evse = Guid.TryParse(evseId, out var id)
            ? await evses.FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            : await evses.FirstOrDefaultAsync(item => item.EvseIdNorm == normalized, cancellationToken);
        if (evse is null)
        {
            return null;
        }

        var location = await db.Locations.AsNoTracking().SingleAsync(item => item.Id == evse.LocationId, cancellationToken);
        var operatorId = evse.OperatorId ?? location.OperatorId;
        var @operator = operatorId is { } key
            ? await db.Operators.AsNoTracking()
                .Where(item => item.Id == key)
                .Select(item => new EvseOperator(item.Id, item.Name, item.OperatorId))
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        var current = await db.EvseStatusCurrent.AsNoTracking().FirstOrDefaultAsync(item => item.EvseId == evse.Id, cancellationToken);
        var sources = await db.SourceRecords.AsNoTracking()
            .Where(record => (record.EntityType == EntityType.EVSE && record.EntityId == evse.Id)
                || (record.EntityType == EntityType.LOCATION && record.EntityId == location.Id))
            .OrderBy(record => record.EntityType).ThenBy(record => record.SourceKey).ThenBy(record => record.ExternalId)
            .ToListAsync(cancellationToken);
        var provenance = await db.AttributeOrigins.AsNoTracking()
            .Where(origin => (origin.EntityType == EntityType.EVSE && origin.EntityId == evse.Id)
                || (origin.EntityType == EntityType.LOCATION && origin.EntityId == location.Id))
            .OrderBy(origin => origin.EntityType).ThenBy(origin => origin.Attribute).ThenBy(origin => origin.SourceKey)
            .ToListAsync(cancellationToken);
        var maxAge = TimeSpan.FromMinutes(config.Get<int>(SettingsCatalog.MapStatusMaxAgeMinutes));
        var status = current is null
            ? new EvseCurrentStatus(nameof(EvseStatus.UNKNOWN), null, null, null, true)
            : new EvseCurrentStatus(
                current.Status.ToString(), current.Since, current.ObservedAt, current.SourceKey, current.IsStale(timeProvider.GetUtcNow(), maxAge));

        return new EvseView(
            evse.Id,
            evse.EvseIdDisplay,
            evse.IdentityKind == IdentityKind.EVSE_ID,
            evse.Lifecycle.ToString(),
            new EvseLocation(
                location.Id,
                location.Name,
                location.Address,
                location.PostalCode,
                location.City,
                location.Country,
                location.Coordinates.Latitude,
                location.Coordinates.Longitude,
                location.RegionCode,
                location.Access.ToString(),
                ParseOpeningTimes(location.OpeningTimes),
                new LocationFacilities(location.GreenEnergy, location.FreeParking, location.RoofedParking, location.BarrierFreeParkingSpaces, location.PriceUrl)),
            @operator,
            evse.PhysicalReference,
            evse.MaxPowerKw,
            evse.PowerType?.ToString(),
            [.. evse.Connectors.OrderBy(connector => connector.Ordinal).Select(connector => new ConnectorDetail(
                connector.Standard.ToString(), connector.Format?.ToString(), connector.PowerType?.ToString(), connector.MaxPowerKw))],
            [.. evse.AuthMethods.Select(method => method.ToString())],
            evse.Roaming,
            status,
            evse.FirstSeen,
            evse.LastSeen,
            evse.LastConfirmedAt,
            [.. sources.Select(record => new SourceReference(
                record.SourceKey, record.EntityType.ToString(), record.ExternalId, record.FirstSeen, record.LastSeen))],
            [.. provenance.Select(origin => new AttributeProvenance(origin.EntityType.ToString(), origin.Attribute, origin.SourceKey, origin.ObservedAt))]);
    }

    /// <summary>The OCPI <c>Hours</c> JSON written by the ingest (<c>twentyfourseven</c>, <c>description</c>).</summary>
    private static OpeningTimes? ParseOpeningTimes(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new OpeningTimes(
            root.TryGetProperty("twentyfourseven", out var allDay) && allDay.ValueKind == JsonValueKind.True,
            root.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String ? description.GetString() : null);
    }

    public async Task<EvsePrices> PricesAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var current = await db.EvsePriceCurrent.AsNoTracking()
            .Where(price => price.EvseId == id && price.TariffKind == TariffKind.AD_HOC)
            .OrderByDescending(price => price.ObservedAt)
            .FirstOrDefaultAsync(cancellationToken);
        // Prices change rarely: all observations of the EVSE, turned into periods per source.
        var observations = await db.PriceObservations.AsNoTracking()
            .Where(observation => observation.EvseId == id && observation.TariffKind == TariffKind.AD_HOC)
            .OrderBy(observation => observation.Time)
            .ToListAsync(cancellationToken);
        var history = observations
            .GroupBy(observation => observation.SourceKey)
            .SelectMany(source =>
            {
                var list = source.ToList();
                return list.Select((observation, index) => new PricePeriod(
                    View(observation.SourceKey, observation.Components), observation.Time, index + 1 < list.Count ? list[index + 1].Time : null));
            })
            .Where(period => period.From < to && (period.To is null || period.To > from))
            .OrderBy(period => period.From)
            .ToList();
        return new EvsePrices(
            current is null ? null : new CurrentPrice(View(current.SourceKey, current.Components), current.Since, current.ObservedAt),
            history,
            await EmpPricesAsync(id, cancellationToken));
    }

    /// <summary>
    /// The EMP tariffs valid now that apply at the EVSE (own network by operator, otherwise roaming), with the energy price
    /// for its current type: DC if the EVSE or one of its connectors is DC, otherwise AC. There are few tariffs; they are
    /// filtered in memory.
    /// </summary>
    private async Task<IReadOnlyList<EmpPrice>> EmpPricesAsync(Guid id, CancellationToken cancellationToken)
    {
        var evse = await TariffTargets(db.Evses.Where(item => item.Id == id)).SingleAsync(cancellationToken);
        return [.. EmpPrices(await ValidTariffsAsync(cancellationToken), evse, PriceExample.Kwh, PriceExample.Minutes)];
    }

    public async Task<LocationPricesPage> LocationPricesAsync(
        IReadOnlyList<Guid> locationIds, decimal kwh, int minutes, IReadOnlyCollection<string>? cards, CancellationToken cancellationToken)
    {
        var ids = locationIds.Distinct().ToList();
        // Three batch queries for all locations: their EVSEs, the ad-hoc prices of these, the tariffs valid now.
        var evses = await TariffTargets(db.Evses.Where(evse => ids.Contains(evse.LocationId) && evse.Lifecycle == Lifecycle.ACTIVE))
            .ToListAsync(cancellationToken);
        var evseIds = evses.Select(evse => evse.Id).ToList();
        var adHoc = (await db.EvsePriceCurrent.AsNoTracking()
                .Where(price => evseIds.Contains(price.EvseId) && price.TariffKind == TariffKind.AD_HOC)
                .ToListAsync(cancellationToken))
            .GroupBy(price => price.EvseId)
            .ToDictionary(prices => prices.Key, prices => prices.MaxBy(price => price.ObservedAt)!);
        var tariffs = (await ValidTariffsAsync(cancellationToken)).Where(row => cards is null || cards.Contains(row.Key)).ToList();
        var byLocation = evses.ToLookup(evse => evse.LocationId);

        var locations = ids.Select(id =>
        {
            // Ties go to the first EVSE-ID.
            var here = byLocation[id].OrderBy(evse => evse.EvseId, StringComparer.Ordinal).ToList();
            var cheapestAdHoc = here
                .Where(evse => adHoc.ContainsKey(evse.Id))
                .Select(evse => (evse.EvseId, Price: adHoc[evse.Id].Components))
                .Select(item => new LocationAdHocPrice(item.Price.CostOf(kwh, minutes), item.Price.Currency, item.EvseId))
                .OrderBy(price => price.Cost)
                .FirstOrDefault();
            var cheapestCard = here
                .Select(evse => (evse.EvseId, Card: EmpPrices(tariffs, evse, kwh, minutes).FirstOrDefault()))
                .Where(item => item.Card is not null)
                .OrderBy(item => item.Card!.Price.ExampleCost)
                .Select(item => new LocationCardPrice(
                    item.Card!.Emp, item.Card.EmpName, item.Card.TariffId, item.Card.Tariff, item.Card.Price.ExampleCost, item.Card.Price.Currency, item.EvseId))
                .FirstOrDefault();
            return new LocationPrices(id, cheapestAdHoc, cheapestCard);
        }).ToList();

        var sources = adHoc.Values.Select(price => price.SourceKey).Distinct().ToList();
        var attribution = await db.Sources
            .Where(source => sources.Contains(source.Key))
            .OrderBy(source => source.Key)
            .Select(source => new SourceAttribution(source.Key, source.AttributionText, source.License))
            .ToListAsync(cancellationToken);
        return new LocationPricesPage(locations, attribution);
    }

    /// <summary>What decides which tariffs apply at an EVSE and with which energy price.</summary>
    private sealed record TariffTarget(Guid Id, Guid LocationId, string EvseId, PowerType? PowerType, bool Dc, bool? Roaming, Guid? OperatorId)
    {
        /// <summary>The current type: DC if the EVSE or one of its connectors is DC.</summary>
        public PowerType? CurrentType => PowerType ?? (Dc ? Domain.Charging.PowerType.DC : null);
    }

    private sealed record TariffRow(Tariff Tariff, string Key, string Name);

    private IQueryable<TariffTarget> TariffTargets(IQueryable<Evse> evses) =>
        evses.AsNoTracking().Select(item => new TariffTarget(
            item.Id,
            item.LocationId,
            item.EvseIdDisplay,
            item.PowerType,
            item.Connectors.Any(connector => connector.PowerType == PowerType.DC),
            item.Roaming,
            item.OperatorId ?? db.Locations.Where(location => location.Id == item.LocationId).Select(location => location.OperatorId).FirstOrDefault()));

    /// <summary>The tariffs valid now with their EMP; there are few, they are filtered in memory.</summary>
    private async Task<List<TariffRow>> ValidTariffsAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return await db.Tariffs.AsNoTracking()
            .Where(tariff => tariff.ValidFrom <= now && (tariff.ValidTo == null || tariff.ValidTo > now))
            .Join(db.Emps, tariff => tariff.EmpId, emp => emp.Id, (tariff, emp) => new TariffRow(tariff, emp.Key, emp.Name))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The tariffs that apply at the EVSE (own network by operator, otherwise roaming), with the energy price for its
    /// current type, cheapest session first.
    /// </summary>
    private static IEnumerable<EmpPrice> EmpPrices(IEnumerable<TariffRow> tariffs, TariffTarget evse, decimal kwh, int minutes) =>
        tariffs
            .Where(row => row.Tariff.AppliesTo(evse.OperatorId, evse.Roaming))
            .Select(row => new EmpPrice(
                row.Key, row.Name, row.Tariff.Id, row.Tariff.Name, row.Tariff.MonthlyFee, View($"emp:{row.Key}", row.Tariff.ComponentsFor(evse.CurrentType), kwh, minutes)))
            .OrderBy(price => price.Price.ExampleCost)
            .ThenBy(price => price.EmpName, StringComparer.Ordinal)
            .ThenBy(price => price.Tariff, StringComparer.Ordinal);

    private static PriceView View(string source, PriceComponents price, decimal kwh = PriceExample.Kwh, int minutes = PriceExample.Minutes) =>
        new(source, price.Currency, price.PricePerKwh, price.PricePerMinute, price.SessionFee, price.BlockingFeePerMinute,
            price.BlockingAfterMinutes, price.FreeOfCharge, price.CostOf(kwh, minutes));

    public async Task<IReadOnlyList<StatusInterval>> StatusHistoryAsync(
        Guid id, DateTimeOffset from, DateTimeOffset to, string? source, CancellationToken cancellationToken)
    {
        var intervals = await db.EvseStatusIntervals.AsNoTracking()
            .Where(interval => interval.EvseId == id
                && interval.ValidFrom < to
                && (interval.ValidTo == null || interval.ValidTo > from)
                && (source == null || interval.SourceKey == source))
            .OrderBy(interval => interval.ValidFrom)
            .ToListAsync(cancellationToken);
        return [.. intervals.Select(interval => new StatusInterval(
            interval.Status.ToString(), interval.ValidFrom, interval.ValidTo, interval.SourceKey, interval.Confidence))];
    }

    public Task<IReadOnlyList<OccupancyCell>?> OccupancyAsync(
        Guid id, DateTimeOffset from, DateTimeOffset to, string timeZone, CancellationToken cancellationToken) =>
        OccupancyAsync([id], from, to, timeZone, cancellationToken);

    public async Task<IReadOnlyList<OccupancyCell>?> OccupancyAsync(
        IReadOnlyList<Guid> ids, DateTimeOffset from, DateTimeOffset to, string timeZone, CancellationToken cancellationToken)
    {
        Guid[] evseIds = [.. ids];
        try
        {
            // Fails for an unknown zone even when the EVSE has no observations.
            await db.Database.SqlQuery<DateTime>($"SELECT now() AT TIME ZONE {timeZone} AS \"Value\"").SingleAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == InvalidParameterValue)
        {
            return null;
        }

        var rows = await db.Database
            .SqlQuery<OccupancyRow>($"""
                SELECT extract(isodow FROM bucket AT TIME ZONE {timeZone})::int AS "Weekday",
                       extract(hour FROM bucket AT TIME ZONE {timeZone})::int AS "Hour",
                       sum(obs_charging)::bigint AS "Charging",
                       sum(obs_total)::bigint AS "Total"
                FROM evse_occupancy_hourly
                WHERE evse_id = ANY({evseIds}) AND bucket >= {from} AND bucket < {to}
                GROUP BY 1, 2
                ORDER BY 1, 2
                """)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(row => new OccupancyCell(row.Weekday, row.Hour, row.Charging, row.Total))];
    }

    /// <summary>Sort key of EVSEs without status in the order <see cref="EvseSort.StatusSince"/>: after all others.</summary>
    private static readonly DateTimeOffset NoStatus = new(9999, 12, 31, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// One statement over the active EVSEs of active locations with their station and current status; keyset paging on the
    /// normalized EVSE-ID (byte order, collation C) or on (status since, id). The operator filter compares the parts of the
    /// normalized EVSE-ID, so surrogates never match it.
    /// </summary>
    public async Task<EvseSearchPage> SearchAsync(
        EvseSearchFilter filter, EvseSort sort, EvseCursor? after, int limit, CancellationToken cancellationToken)
    {
        var byId = sort == EvseSort.EvseId;
        var afterKey = byId ? after?.Key : null;
        DateTimeOffset? afterSince = !byId && after is not null
            ? DateTimeOffset.Parse(after.Key, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)
            : null;
        var afterId = after?.Id ?? Guid.Empty;
        string[] statuses = [.. filter.Statuses];
        string[] connectors = [.. filter.Connector];
        Guid? stationId = Guid.TryParse(filter.ChargingStation, out var station) ? station : null;
        var stationLabel = stationId is null && filter.ChargingStation is { } label
            ? string.Concat(label.ToUpperInvariant().Where(character => character is not ('*' or '-') && !char.IsWhiteSpace(character)))
            : null;
        // With a country the prefix of the normalized EVSE-ID uses the text_pattern_ops index.
        var operatorPrefix = filter.OperatorCountry is { } country ? $"{country}{filter.OperatorParty}%" : null;
        var rows = await db.Database.SqlQuery<EvseRow>($"""
            SELECT e.id AS "Id", e.evse_id_display AS "EvseId", e.evse_id_norm AS "EvseIdNorm",
                   l.id AS "LocationId", l.name AS "LocationName", l.city AS "City", l.region_code AS "Region", l.park_id AS "ParkId",
                   s.id AS "StationId", s.label AS "StationLabel",
                   coalesce(c.status, 'UNKNOWN') AS "Status", c.since AS "StatusSince", c.source_key AS "StatusSource",
                   e.max_power_kw AS "MaxPowerKw", coalesce(c.since, {NoStatus}) AS "SortSince"
            FROM evse e
            JOIN location l ON l.id = e.location_id AND l.lifecycle = 'ACTIVE'
            LEFT JOIN charging_station s ON s.id = e.charging_station_id
            LEFT JOIN evse_status_current c ON c.evse_id = e.id
            WHERE e.lifecycle = 'ACTIVE'
              AND ({filter.OperatorParty}::text IS NULL OR (e.identity_kind = 'EVSE_ID' AND substr(e.evse_id_norm, 3, 3) = {filter.OperatorParty}::text))
              AND ({operatorPrefix}::text IS NULL OR e.evse_id_norm LIKE {operatorPrefix}::text)
              AND ({filter.Region}::text IS NULL OR l.region_code = {filter.Region}::text)
              AND (cardinality({statuses}::text[]) = 0 OR coalesce(c.status, 'UNKNOWN') = ANY({statuses}::text[]))
              AND ({filter.MinPowerKw}::numeric IS NULL OR e.max_power_kw >= {filter.MinPowerKw}::numeric)
              AND ({filter.MaxPowerKw}::numeric IS NULL OR e.max_power_kw <= {filter.MaxPowerKw}::numeric)
              AND (cardinality({connectors}::text[]) = 0
                   OR EXISTS (SELECT 1 FROM connector k WHERE k.evse_id = e.id AND k.standard = ANY({connectors}::text[])))
              AND ({filter.StatusSinceBefore}::timestamptz IS NULL OR c.since <= {filter.StatusSinceBefore}::timestamptz)
              AND ({stationId}::uuid IS NULL OR e.charging_station_id = {stationId}::uuid)
              AND ({stationLabel}::text IS NULL OR upper(translate(s.label, '*- ', '')) = {stationLabel}::text)
              AND ({afterKey}::text IS NULL OR e.evse_id_norm COLLATE "C" > {afterKey}::text)
              AND ({afterSince}::timestamptz IS NULL OR (coalesce(c.since, {NoStatus}), e.id) > ({afterSince}::timestamptz, {afterId}))
            ORDER BY CASE WHEN {byId} THEN e.evse_id_norm COLLATE "C" END,
                     CASE WHEN {byId} THEN NULL ELSE coalesce(c.since, {NoStatus}) END,
                     e.id
            LIMIT {limit + 1}
            """).ToListAsync(cancellationToken);
        var page = rows.Take(limit).ToList();
        var last = page.LastOrDefault();
        var next = rows.Count > limit && last is not null
            ? new EvseCursor(sort, byId ? last.EvseIdNorm : last.SortSince.ToString("O", System.Globalization.CultureInfo.InvariantCulture), last.Id)
            : null;
        List<Guid> ids = [.. page.Select(row => row.Id)];
        return new EvseSearchPage(
            [.. page.Select(row => new EvseListItem(
                row.Id,
                row.EvseId,
                new EvseListLocation(row.LocationId, row.LocationName, row.City, row.Region, row.ParkId),
                row.StationId is { } id ? new EvseStationRef(id, row.StationLabel) : null,
                row.Status,
                row.StatusSince,
                row.StatusSource,
                row.MaxPowerKw))],
            next,
            await AttributionAsync(ids, cancellationToken));
    }

    public Task<IReadOnlyList<SourceAttribution>> AttributionAsync(Guid id, CancellationToken cancellationToken) =>
        AttributionAsync([id], cancellationToken);

    private async Task<IReadOnlyList<SourceAttribution>> AttributionAsync(List<Guid> ids, CancellationToken cancellationToken)
    {
        var keys = await db.SourceRecords
            .Where(record => record.EntityType == EntityType.EVSE && ids.Contains(record.EntityId))
            .Select(record => record.SourceKey)
            .Distinct()
            .ToListAsync(cancellationToken);
        return await db.Sources
            .Where(source => keys.Contains(source.Key))
            .OrderBy(source => source.Key)
            .Select(source => new SourceAttribution(source.Key, source.AttributionText, source.License))
            .ToListAsync(cancellationToken);
    }

    /// <summary>One row of the search; property names are the column aliases.</summary>
    private sealed class EvseRow
    {
        public Guid Id { get; set; }

        public string EvseId { get; set; } = null!;

        public string EvseIdNorm { get; set; } = null!;

        public Guid LocationId { get; set; }

        public string? LocationName { get; set; }

        public string? City { get; set; }

        public string? Region { get; set; }

        public Guid? ParkId { get; set; }

        public Guid? StationId { get; set; }

        public string? StationLabel { get; set; }

        public string Status { get; set; } = null!;

        public DateTimeOffset? StatusSince { get; set; }

        public string? StatusSource { get; set; }

        public decimal? MaxPowerKw { get; set; }

        public DateTimeOffset SortSince { get; set; }
    }

    /// <summary>One row of the occupancy query; property names are the column aliases.</summary>
    private sealed class OccupancyRow
    {
        public int Weekday { get; set; }

        public int Hour { get; set; }

        public long Charging { get; set; }

        public long Total { get; set; }
    }
}
