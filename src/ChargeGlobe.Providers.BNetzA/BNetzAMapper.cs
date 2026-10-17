using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Providers.BNetzA;

/// <summary>
/// Anti-corruption layer: rows of the register → ingest model. The register lists one facility ("Ladeeinrichtung")
/// per charging station; the facilities of one operator at identical coordinates form one location (the site),
/// each facility a charging station of it, each of its charge points an EVSE with one connector per listed connector
/// type. The register knows no live status: its <c>Status</c> is the lifecycle of the facility ("In Betrieb"), so
/// the EVSEs carry none; "In Wartung" is passed as INOPERATIVE (table <c>Mapping/status-mapping.json</c>). Broken rows
/// are reported as <see cref="ProviderMappingIssue"/>.
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

    /// <summary>The key of the site a facility belongs to: its coordinates with six decimals; null without valid ones.</summary>
    public static string? SiteKey(FacilityRow row) =>
        RegisterCsv.Decimal(row.Latitude) is { } latitude && RegisterCsv.Decimal(row.Longitude) is { } longitude && IsValid(latitude, longitude)
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Math.Round(latitude, 6)},{Math.Round(longitude, 6)}")
            : null;

    /// <summary>
    /// The facilities at one position split by operator (<see cref="OperatorNames.Same"/>), each group ordered by
    /// facility ID: one location per group.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<(FacilityRow Row, JsonElement Raw)>> Sites(IEnumerable<(FacilityRow Row, JsonElement Raw)> facilities)
    {
        var groups = new List<List<(FacilityRow Row, JsonElement Raw)>>();
        foreach (var facility in facilities.OrderBy(facility => facility.Row.Id, StringComparer.Ordinal))
        {
            var group = groups.FirstOrDefault(group => OperatorNames.Same(group[0].Row.Operator, facility.Row.Operator)
                || group[0].Row.Operator is null && facility.Row.Operator is null);
            if (group is null)
            {
                groups.Add([facility]);
            }
            else
            {
                group.Add(facility);
            }
        }

        return groups;
    }

    /// <summary>One facility on its own (tests and single rows).</summary>
    public ProviderLocationSnapshot? ToLocation(FacilityRow row, JsonElement raw, DateTimeOffset observedAt, ICollection<ProviderMappingIssue> issues) =>
        ToLocation([(row, raw)], observedAt, issues);

    /// <summary>
    /// A site: the facilities of one operator at one position, ordered by ID. The first facility names the site
    /// (its ID is the external ID, its address the address); values the first one lacks come from the others.
    /// </summary>
    public ProviderLocationSnapshot? ToLocation(
        IReadOnlyList<(FacilityRow Row, JsonElement Raw)> facilities, DateTimeOffset observedAt, ICollection<ProviderMappingIssue> issues)
    {
        var first = facilities[0].Row;
        if (RegisterCsv.Decimal(first.Latitude) is not { } latitude || RegisterCsv.Decimal(first.Longitude) is not { } longitude
            || !IsValid(latitude, longitude))
        {
            issues.Add(new ProviderMappingIssue(BNetzAProvider.Key, first.Id, "Facility without valid coordinates."));
            return null;
        }

        var evses = new List<ProviderEvseSnapshot>();
        var evseIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (row, _) in facilities)
        {
            var status = StatusMapping.Map(row.Status);
            var payment = RegisterCsv.Parts(row.PaymentSystems);
            var authMethods = AuthMethods(payment);
            var price = payment.Any(system => FreeOfCharge.Contains(system, StringComparer.OrdinalIgnoreCase))
                ? new PriceComponents("EUR", null, null, null, null, null, true)
                : null;
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
        }

        // The operator's code and its country come from the first EVSE-ID of the site.
        var firstId = evses.Select(evse => EvseId.TryParse(evse.EvseId, out var id) ? id : null).OfType<EvseId>().FirstOrDefault();
        var rows = facilities.Select(facility => facility.Row).ToList();
        return new ProviderLocationSnapshot(
            first.Id,
            rows.Select(row => row.SiteName).FirstOrDefault(name => name is not null),
            new ProviderAddress(
                string.Join(' ', new[] { first.Street, first.HouseNumber }.Where(part => part is not null)) is { Length: > 0 } street ? street : null,
                first.PostalCode,
                first.City,
                BNetzAProvider.Country),
            new GeoPoint((double)latitude, (double)longitude),
            first.Operator,
            firstId?.OperatorId,
            rows.Select(row => row.Parking is { } parking && _parking.TryGetValue(parking, out var access) ? (AccessType?)access : null).FirstOrDefault(access => access is not null),
            rows.Select(OpeningTimes).FirstOrDefault(times => times is not null),
            evses,
            observedAt,
            // The raw record of a site is the list of its facility rows.
            JsonSerializer.SerializeToElement(facilities.Select(facility => facility.Raw), Json),
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
            // The facility is the charging station of the site (docs/03, charging_station).
            row.Id,
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
