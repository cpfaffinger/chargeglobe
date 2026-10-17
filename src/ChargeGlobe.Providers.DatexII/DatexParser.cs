using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChargeGlobe.Providers.DatexII;

/// <summary>
/// Reads the JSON of both publications into the records of <c>Dto.cs</c>, tolerant of the two dialects in use:
/// the AFIR profile of the Mobilithek as MobiData BW serves it (<c>payload</c> wrapper, <c>aegi…</c> publication
/// names, <c>idG</c>, multilingual <c>values[]</c>, ISO times, camel-case enums) and the Java serialization of the
/// E-Control (publication at the root, <c>id</c>, <c>values.value[]</c>, Unix milliseconds, upper-case enums).
/// Wherever the dialects differ, the first spelling found wins. A document without a publication is a
/// <see cref="JsonException"/> (unexpected format, docs/05).
/// </summary>
public static class DatexParser
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Contact data of hotlines and persons; never stored (docs/08).</summary>
    private static readonly string[] ContactFields = ["helpdesk", "contactInformation"];

    /// <summary>The sites of a table publication (every <c>energyInfrastructureTable</c>, in order).</summary>
    public static IReadOnlyList<DatexSite> ParseTable(JsonNode document)
    {
        var root = Root(document);
        var publication = Get(root, "aegiEnergyInfrastructureTablePublication", "energyInfrastructureTablePublication")
            ?? (Get(root, "energyInfrastructureTable") is not null ? root : null)
            ?? throw new JsonException("The document has no EnergyInfrastructureTablePublication.");
        return [.. Items(publication, "energyInfrastructureTable")
            .SelectMany(table => Items(table, "energyInfrastructureSite"))
            .OfType<JsonObject>()
            .Select(Site)];
    }

    /// <summary>The site statuses of a status publication.</summary>
    public static IReadOnlyList<DatexSiteStatus> ParseStatus(JsonNode document)
    {
        var root = Root(document);
        var publication = Get(root, "aegiEnergyInfrastructureStatusPublication", "energyInfrastructureStatusPublication")
            ?? (Get(root, "energyInfrastructureSiteStatus") is not null ? root : null)
            ?? throw new JsonException("The document has no EnergyInfrastructureStatusPublication.");
        return [.. Items(publication, "energyInfrastructureSiteStatus").Select(site => new DatexSiteStatus(
            Id(Get(site, "reference")) ?? string.Empty,
            [.. Items(site, "energyInfrastructureStationStatus")
                .SelectMany(station => Items(station, "refillPointStatus"))
                .Select(status => Get(status, "aegiRefillPointStatus") ?? status)
                .Select(status => new DatexPointStatus(
                    Id(Get(status, "reference")) ?? string.Empty,
                    Value(Get(status, "status")) ?? string.Empty,
                    Time(Get(status, "lastUpdated")),
                    Get(status, "newRates") is { } rates ? Rates(rates) : null,
                    JsonSerializer.SerializeToElement(status, Json)))]))];
    }

    /// <summary>Removes the <c>payload</c> wrapper of the MobiData dialect.</summary>
    private static JsonNode Root(JsonNode document) => Get(document, "payload") ?? document;

    private static DatexSite Site(JsonObject site)
    {
        var location = Get(site, "locationReference");
        var address = Get(Get(Get(Get(location, "locPointLocation"), "locLocationExtensionG"), "FacilityLocation"), "address")
            ?? Get(Get(Get(location, "_LocationReferenceExtension"), "facilityLocation"), "address");
        var coordinates = Get(Get(location, "locAreaLocation"), "coordinatesForDisplay")
            ?? Get(location, "coordinatesForDisplay")
            ?? Get(Get(location, "pointByCoordinates"), "pointCoordinates");
        var lines = Items(address, "addressLine")
            .Select(line => (Type: Normalize(Value(Get(line, "type"))), Text: Text(Get(line, "text"))))
            .ToList();
        var organisation = Get(Get(site, "operator"), "afacAnOrganisation") ?? Get(site, "operator");
        var hours = Get(site, "operatingHours");
        var openAllHours = Get(hours, "afacOpenAllHours", "openAllHours") is not null;
        var openingText = openAllHours ? null : OpeningText(hours);
        foreach (var field in ContactFields)
        {
            site.Remove(field);
        }

        return new DatexSite(
            Id(site) ?? string.Empty,
            Text(Get(site, "name")),
            lines.FirstOrDefault(line => line.Type == "street").Text,
            lines.FirstOrDefault(line => line.Type == "housenumber").Text,
            String(Get(address, "postcode")),
            Text(Get(address, "city")),
            String(Get(address, "countryCode")),
            Double(Get(coordinates, "latitude")),
            Double(Get(coordinates, "longitude")),
            Text(Get(organisation, "name")),
            Items(organisation, "externalIdentifier")
                .Where(identifier => IdentifierType(identifier) == "operatorid")
                .Select(identifier => String(Get(identifier, "identifier")))
                .FirstOrDefault(value => value is not null),
            openAllHours ? true : openingText is null ? null : false,
            openingText,
            [.. Items(site, "energyInfrastructureStation").Select(Station)],
            JsonSerializer.SerializeToElement(site, Json));
    }

    private static DatexStation Station(JsonNode station) =>
        new(
            Id(station) ?? string.Empty,
            Get(station, "authenticationAndIdentificationMethods") is { } methods
                ? [.. Items(methods).Select(Value).OfType<string>()]
                : null,
            [.. Items(station, "refillPoint").Select(point => Get(point, "aegiElectricChargingPoint") ?? point).Select(RefillPoint)]);

    private static DatexRefillPoint RefillPoint(JsonNode point) =>
        new(
            Id(point) ?? string.Empty,
            Items(point, "externalIdentifier")
                .Where(identifier => IdentifierType(identifier) == "evseid")
                .Select(identifier => String(Get(identifier, "identifier")))
                .FirstOrDefault(value => value is not null),
            Value(Get(point, "currentType")),
            [.. Items(point, "availableVoltage").Select(Int).OfType<int>()],
            [.. Items(point, "availableChargingPower").Select(Decimal).OfType<decimal>()],
            [.. Items(point, "connector").Select(connector => new DatexConnector(
                Value(Get(connector, "connectorType")),
                Value(Get(connector, "connectorFormat")),
                Decimal(Get(connector, "maxPowerAtSocket")),
                Int(Get(connector, "voltage")),
                Int(Get(connector, "maximumCurrent"))))],
            [.. Items(point, "electricEnergy").SelectMany(energy => Items(energy, "energyRate")).Select(EnergyRate)]);

    /// <summary><c>EnergyRate</c> of the AFIR profile: policy, currency and the price components.</summary>
    private static DatexRate EnergyRate(JsonNode rate)
    {
        var prices = Items(rate, "energyPrice")
            .Select(price => new DatexPrice(Normalize(Value(Get(price, "priceType"))), Gross(price)))
            .ToList();
        return new DatexRate(
            Normalize(Value(Get(rate, "ratePolicy"))) switch { "adhoc" => "adHoc", "contract" => "contract", _ => null },
            Items(rate, "applicableCurrency").Select(String).FirstOrDefault(value => value is not null),
            prices.Where(price => price.Type != "free").ToList(),
            prices.Any(price => price.Type == "free"));
    }

    /// <summary>
    /// <c>Rates</c> of DATEX II (status publication of the E-Control): the pricing policy says whether charging is free
    /// or paid by contract, the rate lines carry the prices (<c>PER_UNIT</c> per kWh, <c>INCREMENTING_RATE</c> per
    /// increment period, <c>FLAT_RATE</c> per session).
    /// </summary>
    private static DatexRate Rates(JsonNode rates)
    {
        var policies = Items(Get(rates, "energyPricingPolicy"), "pricingPolicy").Select(Value).Select(Normalize).ToList();
        var collections = Items(rates, "rateLineCollection").ToList();
        var prices = collections
            .SelectMany(collection => Items(collection, "rateLine"))
            .Select(line => (Type: Normalize(Value(Get(line, "rateLineType"))), Period: String(Get(line, "incrementPeriod")), Value: Decimal(Get(line, "value"))))
            .Select(line => line.Type switch
            {
                "perunit" => new DatexPrice("priceperkwh", line.Value),
                "incrementingrate" when line.Period is null or "PT1M" => new DatexPrice("priceperminute", line.Value),
                "flatrate" => new DatexPrice("flatrate", line.Value),
                _ => new DatexPrice(line.Type, line.Value),
            })
            .ToList();
        return new DatexRate(
            policies.Contains("contract") && !policies.Any(policy => policy.StartsWith("priceper", StringComparison.Ordinal)) ? "contract" : "adHoc",
            collections.Select(collection => String(Get(collection, "applicableCurrency"))).FirstOrDefault(value => value is not null)
                ?? Items(rates, "applicableCurrency").Select(String).FirstOrDefault(value => value is not null),
            prices,
            policies.Contains("free"));
    }

    /// <summary>The amount of an <c>EnergyPrice</c> including tax: a net amount (<c>taxIncluded: false</c>) grows by its <c>taxRate</c>.</summary>
    private static decimal? Gross(JsonNode price)
    {
        var value = Decimal(Get(price, "value"));
        var included = Get(price, "taxIncluded") is JsonValue flag && flag.TryGetValue<bool>(out var taxIncluded) ? taxIncluded : true;
        // Dividing by 1.000… strips the trailing zeros of the rounded amount.
        return value is { } net && !included && Decimal(Get(price, "taxRate")) is { } rate ? Math.Round(net * (1 + rate / 100m), 4) / 1.000000000000000000000000000000m : value;
    }

    /// <summary>Listed periods as text, e.g. <c>MONDAY 06:00–16:00; TUESDAY 06:00–16:00</c>; null without periods.</summary>
    private static string? OpeningText(JsonNode? hours)
    {
        var periods = Items(Get(hours, "overallPeriod"), "validPeriod")
            .Select(period => (
                Days: string.Join('/', Items(period, "recurringDayWeekMonthPeriod").SelectMany(day => Items(day, "applicableDay")).Select(Value).OfType<string>()),
                Times: string.Join('/', Items(period, "recurringTimePeriodOfDay")
                    .Select(time => $"{TimeOfDay(Get(time, "startTimeOfPeriod"))}–{TimeOfDay(Get(time, "endTimeOfPeriod"))}"))))
            .Select(period => string.Join(' ', new[] { period.Days, period.Times }.Where(part => part.Length > 0)))
            .Where(period => period.Length > 0)
            .ToList();
        return periods.Count > 0 ? string.Join("; ", periods) : null;
    }

    /// <summary>A time of day as <c>HH:mm</c>: from milliseconds since midnight (E-Control) or an ISO time.</summary>
    private static string TimeOfDay(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<long>(out var milliseconds)
            ? TimeSpan.FromMilliseconds(milliseconds).ToString(@"hh\:mm", CultureInfo.InvariantCulture)
            : String(node) is { Length: >= 5 } text ? text[..5] : "?";

    private static string IdentifierType(JsonNode identifier)
    {
        var type = Get(identifier, "typeOfIdentifier");
        var extended = String(Get(type, "extendedValueG"));
        return Normalize(extended ?? Value(type));
    }

    /// <summary><c>idG</c> (MobiData) or <c>id</c> (E-Control).</summary>
    private static string? Id(JsonNode? node) => String(Get(node, "idG", "id"));

    /// <summary>The first of the properties that exists.</summary>
    private static JsonNode? Get(JsonNode? node, params string[] names)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (obj.TryGetPropertyValue(name, out var value) && value is not null)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>The elements of an array property (an object counts as a list of one).</summary>
    private static IEnumerable<JsonNode> Items(JsonNode? node, string name) => Items(Get(node, name));

    private static IEnumerable<JsonNode> Items(JsonNode? node) =>
        node switch
        {
            JsonArray array => array.OfType<JsonNode>(),
            null => [],
            _ => [node],
        };

    /// <summary>A <c>MultilingualString</c>: <c>{values: [{lang, value}]}</c> or <c>{values: {value: [{lang, value}]}}</c>; the first value.</summary>
    private static string? Text(JsonNode? multilingual)
    {
        var values = Get(multilingual, "values");
        var entries = values is JsonArray ? Items(values) : Items(values, "value");
        return entries.Select(entry => String(Get(entry, "value"))).FirstOrDefault(value => value is not null);
    }

    /// <summary>An enumeration value: <c>{value: "x"}</c> or <c>"x"</c>.</summary>
    private static string? Value(JsonNode? node) => node is JsonObject ? String(Get(node, "value")) : String(node);

    private static string? String(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static double? Double(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    private static decimal? Decimal(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<decimal>(out var number) ? number : null;

    private static int? Int(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) ? (int)Math.Round(number) : null;

    /// <summary>A time stamp: ISO 8601 text or Unix milliseconds.</summary>
    private static DateTimeOffset? Time(JsonNode? node) =>
        node is JsonValue value
            ? value.TryGetValue<long>(out var milliseconds) ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                : value.TryGetValue<string>(out var text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed
                : null
            : null;

    /// <summary>Lower case without underscores, so that <c>outOfOrder</c> and <c>OUT_OF_ORDER</c> meet.</summary>
    public static string Normalize(string? value) => (value ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
}
