using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Providers.EControl;

/// <summary>
/// Anti-corruption layer: E-Control records → ingest model. Single broken records are reported as
/// <see cref="ProviderMappingIssue"/> and skipped; unknown values stay null or UNKNOWN.
/// </summary>
public sealed class EControlMapper
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IReadOnlyDictionary<string, ConnectorMappingEntry> _connectors;

    private readonly IReadOnlyDictionary<string, AuthMethod> _authMethods;

    public EControlMapper(
        StatusMapping statusMapping,
        IReadOnlyDictionary<string, ConnectorMappingEntry> connectorMapping,
        IReadOnlyDictionary<string, AuthMethod> authMapping)
    {
        StatusMapping = statusMapping;
        _connectors = new Dictionary<string, ConnectorMappingEntry>(connectorMapping, StringComparer.OrdinalIgnoreCase);
        _authMethods = new Dictionary<string, AuthMethod>(authMapping, StringComparer.OrdinalIgnoreCase);
    }

    public StatusMapping StatusMapping { get; }

    /// <summary>The mapping tables shipped with the adapter (<c>Mapping/*.json</c>).</summary>
    public static EControlMapper CreateDefault() =>
        new(
            StatusMapping.FromJson(ReadResource("status-mapping.json")),
            JsonSerializer.Deserialize<Dictionary<string, ConnectorMappingEntry>>(ReadResource("connector-mapping.json"), Json)!,
            JsonSerializer.Deserialize<Dictionary<string, AuthMethod>>(ReadResource("auth-mapping.json"), Json)!);

    /// <summary>External ID of a station: <c>&lt;country&gt;*&lt;operator&gt;*&lt;stationId&gt;</c>.</summary>
    public static string? StationExternalId(StationDto station) =>
        station is { CountryId: { } country, OperatorId: { } @operator, StationId: { } id } ? $"{country}*{@operator}*{id}" : null;

    public ProviderLocationSnapshot? ToLocation(
        StationDto station, JsonElement raw, DateTimeOffset observedAt, ICollection<ProviderMappingIssue> issues)
    {
        var externalId = StationExternalId(station);
        if (externalId is null)
        {
            issues.Add(new ProviderMappingIssue(EControlProvider.Key, null, "Station without country, operator or station ID."));
            return null;
        }

        if (station.Location is not { Lat: { } lat, Lon: { } lon } || !IsValid(lat, lon))
        {
            issues.Add(new ProviderMappingIssue(EControlProvider.Key, externalId, "Station without valid coordinates."));
            return null;
        }

        return new ProviderLocationSnapshot(
            externalId,
            station.Label,
            // The register covers Austria; the country of the EVSE-ID (e.g. CH*ECU) is the operator's.
            new ProviderAddress(station.Street, station.PostCode, station.City, EControlProvider.Country),
            new GeoPoint(lat, lon),
            station.OperatorName,
            station.OperatorId,
            // The register lists publicly accessible charge points only (BGBl. I Nr. 38/2018, § 3 Abs. 5).
            AccessType.PUBLIC,
            OpeningTimes(station.OpeningHours),
            [.. Points(station).Select((point, index) => ToEvse(externalId, point, index))],
            observedAt,
            raw,
            station.CountryId,
            new ProviderFacilities(
                station.GreenEnergy, station.FreeParking, station.RoofedParking, station.BarrierFreeParkingPlaces, Url(station.PriceUrl)));
    }

    /// <summary>The canonical standard and format of a connector key of either vocabulary; unknown keys map to UNKNOWN.</summary>
    public ConnectorMappingEntry Connector(string key)
    {
        key = key.Trim();
        return _connectors.TryGetValue(key, out var entry)
            ? entry
            : Enum.GetNames<ConnectorStandard>().FirstOrDefault(name => name.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } name
                ? new ConnectorMappingEntry(Enum.Parse<ConnectorStandard>(name), null)
                : new ConnectorMappingEntry(ConnectorStandard.UNKNOWN, null);
    }

    /// <summary>Known authentication modes in the order of <see cref="AuthMethod"/>, without duplicates; unknown ones are dropped.</summary>
    public IReadOnlyList<AuthMethod>? AuthMethods(IReadOnlyList<string>? modes) =>
        modes is null
            ? null
            : [.. modes.Select(mode => _authMethods.TryGetValue(mode.Trim(), out var method) ? (AuthMethod?)method : null)
                .OfType<AuthMethod>()
                .Distinct()
                .Order()];

    public IEnumerable<ProviderEvseStatus> ToStatuses(StationDto station, DateTimeOffset observedAt) =>
        StationExternalId(station) is not { } externalId
            ? []
            : Points(station).Select((point, index) => new ProviderEvseStatus(
                EvseExternalId(externalId, point, index),
                point.EvseId,
                null,
                StatusMapping.Map(point.Status),
                point.Status ?? string.Empty,
                observedAt,
                null));

    /// <summary>Ad-hoc price per charge point; cent values become euros.</summary>
    public IEnumerable<ProviderPrice> ToPrices(StationDto station, JsonElement raw, DateTimeOffset observedAt) =>
        StationExternalId(station) is not { } externalId
            ? []
            : Points(station).Select((point, index) => point.FreeOfCharge == true
                ? new ProviderPrice(
                    EvseExternalId(externalId, point, index), point.EvseId, null, null, "EUR",
                    0m, null, 0m, null, null, null, "Ad-hoc (free of charge)", Url(station.PriceUrl), observedAt, raw)
                : new ProviderPrice(
                    EvseExternalId(externalId, point, index), point.EvseId, null, null, "EUR",
                    Euro(point.PriceCentKwh), Euro(point.PriceCentMin), Euro(point.StartFeeCent),
                    Euro(point.BlockingFeeCentMin), point.BlockingFeeFromMinute, null, "Ad-hoc", Url(station.PriceUrl), observedAt, raw));

    private ProviderEvseSnapshot ToEvse(string stationExternalId, PointDto point, int index)
    {
        var powerType = PowerTypeOf(point.ElectricityType);
        var maxPowerKw = point.CapacityKw is { } kw ? (decimal?)Math.Round((decimal)kw, 2) : null;
        return new ProviderEvseSnapshot(
            EvseExternalId(stationExternalId, point, index),
            point.EvseId,
            null,
            null,
            maxPowerKw,
            powerType,
            [.. (point.ConnectorType ?? []).Where(type => type.Key is not null).Select(type => ToConnector(type.Key!, powerType, maxPowerKw))],
            point.Status is null ? null : StatusMapping.Map(point.Status),
            point.Status,
            AuthMethods(point.AuthenticationMode),
            point.Roaming,
            AdHocPrice(point));
    }

    /// <summary>The ad-hoc price of a point in euros; null if the source tells none.</summary>
    private static PriceComponents? AdHocPrice(PointDto point) =>
        point.FreeOfCharge == true
            ? new PriceComponents("EUR", null, null, null, null, null, true)
            : point is { PriceCentKwh: null, PriceCentMin: null, StartFeeCent: null, BlockingFeeCentMin: null }
                ? null
                : new PriceComponents(
                    "EUR", Euro(point.PriceCentKwh), Euro(point.PriceCentMin), Euro(point.StartFeeCent), Euro(point.BlockingFeeCentMin),
                    point.BlockingFeeFromMinute, false);

    private ProviderConnectorSnapshot ToConnector(string key, PowerType? powerType, decimal? maxPowerKw)
    {
        var (standard, format) = Connector(key);
        return new ProviderConnectorSnapshot(standard, format, powerType, maxPowerKw, null, null, key);
    }

    /// <summary>The EVSE-ID; for points without one, the station ID with the position in the list.</summary>
    private static string EvseExternalId(string stationExternalId, PointDto point, int index) =>
        point.EvseId ?? $"{stationExternalId}#{index + 1}";

    private static IEnumerable<PointDto> Points(StationDto station) => station.Points ?? [];

    /// <summary>Only an unambiguous electricity type counts.</summary>
    private static PowerType? PowerTypeOf(IReadOnlyList<string>? types) =>
        types is [var single] && Enum.GetNames<PowerType>().Contains(single) ? Enum.Parse<PowerType>(single) : null;

    private static ProviderOpeningTimes? OpeningTimes(IReadOnlyList<OpeningHoursDto>? hours) =>
        hours switch
        {
            null or [] => null,
            [{ FromWeekday: "MONDAY", FromTime: "00:00", ToWeekday: "SUNDAY", ToTime: "24:00" }] => new ProviderOpeningTimes(true, null),
            _ => new ProviderOpeningTimes(
                false,
                string.Join("; ", hours.Select(range => $"{range.FromWeekday} {range.FromTime}–{range.ToWeekday} {range.ToTime}"))),
        };

    private static decimal? Euro(decimal? cent) => cent / 100m;

    /// <summary>In range and not 0/0, the placeholder of records without a position.</summary>
    private static bool IsValid(double latitude, double longitude) =>
        latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180 && (latitude, longitude) != (0, 0);

    /// <summary>An absolute http(s) URL; a bare host with path (<c>www.example.at/tarife</c>) gets <c>https://</c>.</summary>
    private static string? Url(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var candidate = text.Contains("://", StringComparison.Ordinal) ? text : $"https://{text}";
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.Host.Contains('.', StringComparison.Ordinal)
            ? uri.ToString()
            : null;
    }

    private static string ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>One row of the connector table: OCPI standard and, if the source says it, socket or cable.</summary>
public sealed record ConnectorMappingEntry(ConnectorStandard Standard, ConnectorFormat? Format);
