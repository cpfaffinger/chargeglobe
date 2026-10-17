using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Providers.BNetzA;

/// <summary>
/// Anti-corruption layer: rows of the register → ingest model. A facility becomes a location, each of its charge
/// points an EVSE with one connector per listed connector type. The register knows no live status: its
/// <c>Status</c> is the lifecycle of the facility ("In Betrieb"), so the EVSEs carry none; "In Wartung" is passed
/// as INOPERATIVE (table <c>Mapping/status-mapping.json</c>). Broken rows are reported as <see cref="ProviderMappingIssue"/>.
/// </summary>
public sealed class BNetzAMapper
{
    /// <summary>Values of <c>Bezahlsysteme</c> that mean charging is free of charge.</summary>
    public static readonly IReadOnlyList<string> FreeOfCharge = ["Kostenlos", "Kostenlos (Registrierung)"];

    /// <summary>Value of <c>Öffnungszeiten</c> for round-the-clock access.</summary>
    public const string OpenAllHours = "247";

    /// <summary>Value of <c>Öffnungszeiten</c> with days and times in the next two columns.</summary>
    public const string OpenRestricted = "Eingeschränkt";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IReadOnlyDictionary<string, ConnectorEntry> _connectors;

    private readonly IReadOnlyDictionary<string, IReadOnlyList<AuthMethod>> _authMethods;

    private readonly IReadOnlyDictionary<string, AccessType> _parking;

    public BNetzAMapper(
        StatusMapping statusMapping,
        IReadOnlyDictionary<string, ConnectorEntry> connectorMapping,
        IReadOnlyDictionary<string, IReadOnlyList<AuthMethod>> authMapping,
        IReadOnlyDictionary<string, AccessType> parkingMapping)
    {
        StatusMapping = statusMapping;
        _connectors = new Dictionary<string, ConnectorEntry>(connectorMapping, StringComparer.OrdinalIgnoreCase);
        _authMethods = new Dictionary<string, IReadOnlyList<AuthMethod>>(authMapping, StringComparer.OrdinalIgnoreCase);
        _parking = new Dictionary<string, AccessType>(parkingMapping, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Lifecycle of the facility → OCPI; UNKNOWN means "no status" (the facility is in operation).</summary>
    public StatusMapping StatusMapping { get; }

    /// <summary>The mapping tables shipped with the adapter (<c>Mapping/*.json</c>).</summary>
    public static BNetzAMapper CreateDefault() =>
        new(
            StatusMapping.FromJson(ReadResource("status-mapping.json")),
            JsonSerializer.Deserialize<Dictionary<string, ConnectorEntry>>(ReadResource("connector-mapping.json"), Json)!,
            JsonSerializer.Deserialize<Dictionary<string, IReadOnlyList<AuthMethod>>>(ReadResource("auth-mapping.json"), Json)!,
            JsonSerializer.Deserialize<Dictionary<string, AccessType>>(ReadResource("parking-mapping.json"), Json)!);

    public ProviderLocationSnapshot? ToLocation(FacilityRow row, JsonElement raw, DateTimeOffset observedAt, ICollection<ProviderMappingIssue> issues)
    {
        if (RegisterCsv.Decimal(row.Latitude) is not { } latitude || RegisterCsv.Decimal(row.Longitude) is not { } longitude
            || !IsValid(latitude, longitude))
        {
            issues.Add(new ProviderMappingIssue(BNetzAProvider.Key, row.Id, "Facility without valid coordinates."));
            return null;
        }

        var status = StatusMapping.Map(row.Status);
        var payment = RegisterCsv.Parts(row.PaymentSystems);
        var authMethods = AuthMethods(payment);
        var price = payment.Any(system => FreeOfCharge.Contains(system, StringComparer.OrdinalIgnoreCase))
            ? new PriceComponents("EUR", null, null, null, null, null, true)
            : null;
        var evses = new List<ProviderEvseSnapshot>();
        var evseIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var point in row.Points)
        {
            var evseId = EvseId.TryParse(point.EvseId, out var parsed) ? parsed : null;
            if (evseId is not null && !evseIds.Add(evseId.Normalized))
            {
                issues.Add(new ProviderMappingIssue(BNetzAProvider.Key, row.Id, $"EVSE-ID {evseId.Display} listed twice; point {point.Number} gets a surrogate key."));
                evseId = null;
            }

            evses.Add(ToEvse(row, point, evseId, status, authMethods, price));
        }

        // The operator's code and its country come from the first EVSE-ID of the facility.
        var firstId = evses.Select(evse => EvseId.TryParse(evse.EvseId, out var id) ? id : null).OfType<EvseId>().FirstOrDefault();
        return new ProviderLocationSnapshot(
            row.Id,
            row.SiteName,
            new ProviderAddress(
                string.Join(' ', new[] { row.Street, row.HouseNumber }.Where(part => part is not null)) is { Length: > 0 } street ? street : null,
                row.PostalCode,
                row.City,
                BNetzAProvider.Country),
            new GeoPoint((double)latitude, (double)longitude),
            row.Operator,
            firstId?.OperatorId,
            row.Parking is { } parking && _parking.TryGetValue(parking, out var access) ? access : null,
            OpeningTimes(row),
            evses,
            observedAt,
            raw,
            firstId?.CountryCode);
    }

    /// <summary>The canonical standard, format and current of a connector type of the register; unknown types map to UNKNOWN.</summary>
    public ConnectorEntry Connector(string type) =>
        _connectors.TryGetValue(type.Trim(), out var entry) ? entry : new ConnectorEntry(ConnectorStandard.UNKNOWN, null, null);

    /// <summary>Known payment systems as <see cref="AuthMethod"/>s in enum order, without duplicates; unknown ones are dropped.</summary>
    public IReadOnlyList<AuthMethod>? AuthMethods(IReadOnlyList<string> systems) =>
        systems.Count == 0
            ? null
            : [.. systems.SelectMany(system => _authMethods.TryGetValue(system, out var methods) ? methods : []).Distinct().Order()];

    private ProviderEvseSnapshot ToEvse(
        FacilityRow row, PointRow point, EvseId? evseId, EvseStatus status, IReadOnlyList<AuthMethod>? authMethods, PriceComponents? price)
    {
        var types = RegisterCsv.Parts(point.ConnectorTypes);
        var powers = RegisterCsv.Parts(point.PowerKw).Select(RegisterCsv.Decimal).ToList();
        var connectors = types.Select((type, index) =>
        {
            var (standard, format, powerType) = Connector(type);
            // One power for all connectors of the point, or one per connector.
            var kw = powers.Count == 1 ? powers[0] : index < powers.Count ? powers[index] : null;
            return new ProviderConnectorSnapshot(standard, format, powerType, kw, null, null, type);
        }).ToList();
        var powerTypes = connectors.Select(connector => connector.PowerType).OfType<PowerType>().Distinct().ToList();
        return new ProviderEvseSnapshot(
            evseId?.Normalized ?? $"{row.Id}/LP{point.Number}",
            evseId is null ? null : point.EvseId!.Trim(),
            null,
            null,
            connectors.Max(connector => connector.MaxPowerKw),
            powerTypes is [var single] ? single : null,
            connectors,
            status == EvseStatus.UNKNOWN ? null : status,
            row.Status,
            authMethods,
            null,
            price);
    }

    /// <summary><c>247</c> → open all hours; <c>Eingeschränkt</c> → the days with their times as text; otherwise unknown.</summary>
    private static ProviderOpeningTimes? OpeningTimes(FacilityRow row)
    {
        if (row.OpeningHours == OpenAllHours)
        {
            return new ProviderOpeningTimes(true, null);
        }

        if (row.OpeningHours != OpenRestricted)
        {
            return null;
        }

        var days = RegisterCsv.Parts(row.OpeningDays);
        var times = RegisterCsv.Parts(row.OpeningTimes);
        var description = days.Count == times.Count && days.Count > 0
            ? string.Join("; ", days.Zip(times, (day, time) => $"{day} {time}"))
            : string.Join("; ", days.Concat(times));
        return new ProviderOpeningTimes(false, description.Length > 0 ? description : null);
    }

    /// <summary>In range and not 0/0, the placeholder of records without a position.</summary>
    private static bool IsValid(decimal latitude, decimal longitude) =>
        latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180 && (latitude, longitude) != (0, 0);

    private static string ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"bnetza-{name}")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>One row of the connector table: OCPI standard, socket or cable, and the current.</summary>
public sealed record ConnectorEntry(ConnectorStandard Standard, ConnectorFormat? Format, PowerType? PowerType);
