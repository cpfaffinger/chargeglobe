using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Providers.OpenChargeMap;

/// <summary>
/// Anti-corruption layer: Open Charge Map sites → ingest model. A site becomes a location; every connection becomes as
/// many EVSEs as its <c>Quantity</c>, each with one connector. OCM knows no live status (its status types are
/// reported by users, often days old), so the EVSEs carry none; planned and removed sites and connections are skipped.
/// Broken records are reported as <see cref="ProviderMappingIssue"/>.
/// </summary>
public sealed class OpenChargeMapMapper
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IReadOnlyDictionary<string, OcmConnector> _connectors;

    private readonly IReadOnlyDictionary<string, AccessType> _usage;

    public OpenChargeMapMapper(
        StatusMapping statusMapping,
        IReadOnlyDictionary<string, OcmConnector> connectorMapping,
        IReadOnlyDictionary<string, AccessType> usageMapping)
    {
        StatusMapping = statusMapping;
        _connectors = connectorMapping;
        _usage = usageMapping;
    }

    /// <summary>OCM status type IDs (as text) → OCPI; decides only which sites and connections are skipped.</summary>
    public StatusMapping StatusMapping { get; }

    /// <summary>The mapping tables shipped with the adapter (<c>Mapping/*.json</c>).</summary>
    public static OpenChargeMapMapper CreateDefault() =>
        new(
            StatusMapping.FromJson(ReadResource("status-mapping.json")),
            JsonSerializer.Deserialize<Dictionary<string, OcmConnector>>(ReadResource("connector-mapping.json"), Json)!,
            JsonSerializer.Deserialize<Dictionary<string, AccessType>>(ReadResource("usage-mapping.json"), Json)!);

    public ProviderLocationSnapshot? ToLocation(PoiDto poi, JsonElement raw, DateTimeOffset observedAt, ICollection<ProviderMappingIssue> issues)
    {
        var externalId = poi.ID.ToString(CultureInfo.InvariantCulture);
        if (poi.AddressInfo is not { Latitude: { } lat, Longitude: { } lon } address || !IsValid(lat, lon))
        {
            issues.Add(new ProviderMappingIssue(OpenChargeMapProvider.Key, externalId, "Site without valid coordinates."));
            return null;
        }

        if (Gone(poi.StatusTypeID))
        {
            issues.Add(new ProviderMappingIssue(OpenChargeMapProvider.Key, externalId, $"Site is planned or removed (status type {poi.StatusTypeID})."));
            return null;
        }

        var evses = (poi.Connections ?? [])
            .Where(connection => !Gone(connection.StatusTypeID))
            .SelectMany(connection => ToEvses(externalId, connection))
            .ToList();
        // The operator's three-character ID comes from an EVSE-ID among the references, if there is one.
        var evseId = evses.Select(evse => EvseId.TryParse(evse.EvseId, out var id) ? id : null).OfType<EvseId>().FirstOrDefault();
        return new ProviderLocationSnapshot(
            externalId,
            Blank(address.Title),
            new ProviderAddress(
                Blank(address.AddressLine1),
                Blank(address.Postcode),
                Blank(address.Town),
                Blank(address.Country?.ISOCode)?.ToUpperInvariant() ?? OpenChargeMapProvider.Country),
            new GeoPoint(lat, lon),
            OperatorName(poi.OperatorInfo),
            evseId?.OperatorId,
            poi.UsageTypeID is { } usage && _usage.TryGetValue(usage.ToString(CultureInfo.InvariantCulture), out var access) ? access : null,
            null,
            evses,
            observedAt,
            raw,
            evseId?.CountryCode);
    }

    private IEnumerable<ProviderEvseSnapshot> ToEvses(string siteId, ConnectionDto connection)
    {
        var count = Math.Max(1, connection.Quantity ?? 1);
        var key = connection.ConnectionTypeID?.ToString(CultureInfo.InvariantCulture) ?? "0";
        var (standard, format) = _connectors.TryGetValue(key, out var entry) ? entry : new OcmConnector(ConnectorStandard.UNKNOWN, null);
        var powerType = connection.CurrentTypeID switch
        {
            10 => PowerType.AC_1_PHASE,
            20 => PowerType.AC_3_PHASE,
            30 => PowerType.DC,
            _ => (PowerType?)null,
        };
        var maxPowerKw = connection.PowerKW is { } kw and > 0 ? (decimal?)Math.Round((decimal)kw, 2) : null;
        var connector = new ProviderConnectorSnapshot(standard, format, powerType, maxPowerKw, connection.Voltage, connection.Amps, key);
        // A reference is an EVSE-ID only for a single item; with a quantity it names the group.
        var evseId = count == 1 && EvseId.TryParse(connection.Reference, out var parsed) ? parsed.Display : null;
        return Enumerable.Range(1, count).Select(item => new ProviderEvseSnapshot(
            count == 1 ? $"{siteId}#{connection.ID}" : $"{siteId}#{connection.ID}#{item}",
            evseId,
            null,
            null,
            maxPowerKw,
            powerType,
            [connector],
            null,
            null));
    }

    private bool Gone(int? statusTypeId) =>
        statusTypeId is { } id && StatusMapping.Map(id.ToString(CultureInfo.InvariantCulture)) is EvseStatus.PLANNED or EvseStatus.REMOVED;

    /// <summary>Placeholders such as <c>(Unknown Operator)</c> or <c>(Business Owner at Location)</c> are no operator.</summary>
    private static string? OperatorName(OperatorInfoDto? info) =>
        Blank(info?.Title) is { } title && !title.StartsWith('(') ? title : null;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>In range and not 0/0, the placeholder of records without a position.</summary>
    private static bool IsValid(double latitude, double longitude) =>
        latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180 && (latitude, longitude) != (0, 0);

    private static string ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"ocm-{name}")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>One row of the connector table: OCPI standard and, if known, socket or cable.</summary>
public sealed record OcmConnector(ConnectorStandard Standard, ConnectorFormat? Format);
