using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Providers.DatexII;

/// <summary>
/// Anti-corruption layer: DATEX II sites and statuses → ingest model. A site becomes a location, every refill point
/// an EVSE with its connectors; the EVSE-ID comes from the <c>externalIdentifier</c> of the point or from its ID
/// (<c>DE*SHN*E0030119</c>, or embedded as in <c>AT-006-EPI001-AT*006*E01-rp</c>), a synthetic ID such as
/// <c>BNETZA*1002913*1</c> gives none. Vocabularies are looked up without case and underscores, so that both
/// dialects (<c>iec62196T2</c>, <c>IEC_62196_T_2</c>) hit the same row. Broken records are reported as
/// <see cref="ProviderMappingIssue"/>.
/// </summary>
public sealed partial class DatexMapper
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Dictionary<string, ConnectorStandard> _connectors;

    private readonly Dictionary<string, IReadOnlyList<AuthMethod>> _authMethods;

    public DatexMapper(
        StatusMapping statusMapping,
        IReadOnlyDictionary<string, ConnectorStandard> connectorMapping,
        IReadOnlyDictionary<string, IReadOnlyList<AuthMethod>> authMapping)
    {
        StatusMapping = statusMapping;
        _connectors = connectorMapping.ToDictionary(entry => DatexParser.Normalize(entry.Key), entry => entry.Value);
        _authMethods = authMapping.ToDictionary(entry => DatexParser.Normalize(entry.Key), entry => entry.Value);
    }

    /// <summary><c>RefillPointStatusEnum</c> → OCPI (docs/08 section 7).</summary>
    public StatusMapping StatusMapping { get; }

    /// <summary>The mapping tables shipped with the adapter (<c>Mapping/*.json</c>).</summary>
    public static DatexMapper CreateDefault() =>
        new(
            StatusMapping.FromJson(ReadResource("status-mapping.json")),
            JsonSerializer.Deserialize<Dictionary<string, ConnectorStandard>>(ReadResource("connector-mapping.json"), Json)!,
            JsonSerializer.Deserialize<Dictionary<string, IReadOnlyList<AuthMethod>>>(ReadResource("auth-mapping.json"), Json)!);

    /// <summary>
    /// The EVSE-ID a refill point ID carries: the whole ID (<c>DE*SHN*E0030119</c>) or an embedded one
    /// (<c>AT-006-EPI001-AT*006*E01-rp</c>); null for synthetic IDs (<c>BNETZA*1002913*1</c>).
    /// </summary>
    public static EvseId? EvseIdOf(string? pointId)
    {
        // The embedded form first: EvseId.TryParse also reads dashes as separators and would take a whole path for an ID.
        var match = EmbeddedEvseId().Match(pointId ?? string.Empty);
        return match.Success && EvseId.TryParse(match.Value, out var embedded) ? embedded
            : EvseId.TryParse(pointId, out var whole) ? whole
            : null;
    }

    public ProviderLocationSnapshot? ToLocation(string providerKey, DatexSite site, DateTimeOffset observedAt, ICollection<ProviderMappingIssue> issues)
    {
        if (site.Id.Length == 0)
        {
            issues.Add(new ProviderMappingIssue(providerKey, null, "Site without ID."));
            return null;
        }

        if (site is not { Latitude: { } latitude, Longitude: { } longitude } || !IsValid(latitude, longitude))
        {
            issues.Add(new ProviderMappingIssue(providerKey, site.Id, "Site without valid coordinates."));
            return null;
        }

        if (site.Country is null)
        {
            issues.Add(new ProviderMappingIssue(providerKey, site.Id, "Site without country."));
            return null;
        }

        var evses = site.Stations
            .SelectMany(station => station.Points.Select(point => ToEvse(station, point)))
            .ToList();
        // The operator's code: the typed external identifier (DE*EUL) or the prefix of the first EVSE-ID.
        var operatorId = OperatorId(site.OperatorId);
        var firstId = evses.Select(evse => EvseId.TryParse(evse.EvseId, out var id) ? id : null).OfType<EvseId>().FirstOrDefault();
        return new ProviderLocationSnapshot(
            site.Id,
            site.Name,
            new ProviderAddress(
                string.Join(' ', new[] { site.Street, site.HouseNumber }.Where(part => part is not null)) is { Length: > 0 } street ? street : null,
                site.PostalCode,
                site.City,
                site.Country),
            new GeoPoint(latitude, longitude),
            site.OperatorName,
            operatorId?.Id ?? firstId?.OperatorId,
            // AFIR publications list publicly accessible recharging points (Art. 20).
            AccessType.PUBLIC,
            site.OpenAllHours switch
            {
                true => new ProviderOpeningTimes(true, null),
                false => new ProviderOpeningTimes(false, site.OpeningText),
                null => null,
            },
            evses,
            observedAt,
            site.Raw,
            operatorId?.Country ?? firstId?.CountryCode);
    }

    public IEnumerable<ProviderEvseStatus> ToStatuses(DatexSiteStatus site, DateTimeOffset observedAt) =>
        site.Points.Select(point => new ProviderEvseStatus(
            point.Id, EvseIdOf(point.Id)?.Display, null, StatusMapping.Map(point.Status), point.Status, observedAt, point.LastUpdated));

    /// <summary>The ad-hoc prices a status publication carries (<c>newRates</c>); points without rates give none.</summary>
    public IEnumerable<ProviderPrice> ToPrices(DatexSiteStatus site, DateTimeOffset observedAt) =>
        site.Points
            .Select(point => (Point: point, Price: point.Rates is { } rates ? AdHocPrice([rates]) : null))
            .Where(item => item.Price is not null)
            .Select(item => new ProviderPrice(
                item.Point.Id, EvseIdOf(item.Point.Id)?.Display, null, null, item.Price!.Currency,
                item.Price.PricePerKwh, item.Price.PricePerMinute, item.Price.SessionFee, item.Price.BlockingFeePerMinute, item.Price.BlockingAfterMinutes,
                null, item.Price.FreeOfCharge ? "Ad-hoc (free of charge)" : "Ad-hoc", null, observedAt, item.Point.Raw));

    /// <summary>The canonical standard of a connector type of either dialect; unknown types map to UNKNOWN.</summary>
    public ConnectorStandard Connector(string? type) =>
        type is not null && _connectors.TryGetValue(DatexParser.Normalize(type), out var standard) ? standard : ConnectorStandard.UNKNOWN;

    /// <summary>Known authentication methods as <see cref="AuthMethod"/>s in enum order, without duplicates; unknown ones are dropped.</summary>
    public IReadOnlyList<AuthMethod>? AuthMethods(IReadOnlyList<string>? methods) =>
        methods is null
            ? null
            : [.. methods.SelectMany(method => _authMethods.TryGetValue(DatexParser.Normalize(method), out var mapped) ? mapped : []).Distinct().Order()];

    /// <summary>
    /// The ad-hoc price of a point: the first rate with policy <c>adHoc</c> (or without policy); a rate that says
    /// "free" is free of charge, otherwise the components per kWh, per minute and per session.
    /// </summary>
    public static PriceComponents? AdHocPrice(IReadOnlyList<DatexRate> rates)
    {
        var rate = rates.FirstOrDefault(rate => rate.Policy is null or "adHoc");
        if (rate is null)
        {
            return null;
        }

        var currency = Currency(rate.Currency);
        if (rate.Free)
        {
            return new PriceComponents(currency, null, null, null, null, null, true);
        }

        decimal? Of(params string[] types) => rate.Prices.FirstOrDefault(price => types.Contains(price.Type))?.Value;
        var components = new PriceComponents(currency, Of("priceperkwh"), Of("priceperminute"), Of("baseprice", "flatrate"), null, null, false);
        return components is { PricePerKwh: null, PricePerMinute: null, SessionFee: null } ? null : components;
    }

    private ProviderEvseSnapshot ToEvse(DatexStation station, DatexRefillPoint point)
    {
        var evseId = point.ExternalEvseId is { } external && EvseId.TryParse(external, out var typed) ? typed : EvseIdOf(point.Id);
        var powerType = PowerTypeOf(point);
        var connectors = point.Connectors.Select(connector => new ProviderConnectorSnapshot(
            Connector(connector.Type),
            DatexParser.Normalize(connector.Format) switch { "socket" => ConnectorFormat.SOCKET, "cablemode3" or "cable" => ConnectorFormat.CABLE, _ => null },
            powerType,
            Kilowatts(connector.MaxPowerW),
            connector.Voltage,
            connector.Amperage,
            connector.Type)).ToList();
        var maxPowerKw = connectors.Max(connector => connector.MaxPowerKw) ?? (point.PowersW.Count > 0 ? Kilowatts(point.PowersW.Max()) : null);
        return new ProviderEvseSnapshot(
            point.Id,
            evseId?.Display,
            null,
            // No station level: the sources map their site to one station (MobiData BW) or one station to one point (E-Control).
            null,
            maxPowerKw,
            powerType,
            connectors,
            null,
            null,
            AuthMethods(station.AuthMethods),
            null,
            AdHocPrice(point.Rates));
    }

    /// <summary><c>dc</c> → DC; <c>ac</c> with 400 V → three-phase, with 230 V → single-phase, otherwise unknown.</summary>
    private static PowerType? PowerTypeOf(DatexRefillPoint point) =>
        DatexParser.Normalize(point.CurrentType) switch
        {
            "dc" => PowerType.DC,
            "ac" when point.Voltages.Contains(400) => PowerType.AC_3_PHASE,
            "ac" when point.Voltages.Contains(230) => PowerType.AC_1_PHASE,
            _ => null,
        };

    /// <summary><c>DE*EUL</c> → (EUL, DE); null for anything else.</summary>
    private static (string Id, string Country)? OperatorId(string? value) =>
        value?.Trim().ToUpperInvariant().Split('*') is [{ Length: 2 } country, { Length: 3 } id] && country.All(char.IsAsciiLetterUpper) && id.All(char.IsAsciiLetterOrDigit)
            ? (id, country)
            : null;

    /// <summary>An ISO 4217 code as delivered, or the names the E-Control uses.</summary>
    private static string Currency(string? value) =>
        value?.Trim() switch
        {
            null or "" => "EUR",
            { Length: 3 } code => code.ToUpperInvariant(),
            var name when name.Equals("Euro", StringComparison.OrdinalIgnoreCase) => "EUR",
            var other => other,
        };

    private static decimal? Kilowatts(decimal? watts) => watts is { } w and > 0 ? Math.Round(w / 1000m, 2) : null;

    /// <summary>In range and not 0/0, the placeholder of records without a position.</summary>
    private static bool IsValid(double latitude, double longitude) =>
        latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180 && (latitude, longitude) != (0, 0);

    private static string ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"datex-{name}")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"[A-Z]{2}\*[A-Z0-9]{3}\*E[A-Z0-9*]+", RegexOptions.IgnoreCase)]
    private static partial Regex EmbeddedEvseId();
}
