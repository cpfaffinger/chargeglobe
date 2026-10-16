using System.Globalization;
using ChargeGlobe.Api.Auth;
using ChargeGlobe.Api.OpenApi;
using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Application.Locations;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Api.Locations;

/// <param name="Returned">Number of locations in <c>data</c>.</param>
/// <param name="Truncated">True if more locations lie in the box; zoom in or raise <c>limit</c>.</param>
/// <param name="Attribution">Sources of the returned data with the attribution their terms require.</param>
public sealed record LocationsMeta(int Returned, int Limit, bool Truncated, IReadOnlyList<SourceAttribution> Attribution);

public sealed record LocationsResponse(IReadOnlyList<LocationSummary> Data, LocationsMeta Meta);

/// <param name="Park">The other locations of the charging park, nearest first; empty if the location stands alone.</param>
public sealed record LocationDetailData(LocationSummary Location, IReadOnlyList<EvseDetail> Evses, IReadOnlyList<LocationSummary> Park);

public sealed record AttributionMeta(IReadOnlyList<SourceAttribution> Attribution);

public sealed record LocationDetailResponse(LocationDetailData Data, AttributionMeta Meta);

public static class LocationsEndpoints
{
    public const int DefaultLimit = 500;
    public const int MaxLimit = 2000;
    public const int MaxPowerKw = 1000;
    public const int MaxOperatorLength = 100;

    public static readonly SourceAttribution ExampleAttribution = new(
        "econtrol",
        "Datenquelle: Ladestellenverzeichnis der E-Control (ladestellen.at)",
        "E-Control API terms of use (free, registration required); redistribution only with written consent");

    public static readonly LocationSummary ExampleLocation = new(
        Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4e"),
        "Parkgarage Stephansplatz",
        "Stephansplatz 1",
        "1010",
        "Wien",
        "AT",
        48.2085,
        16.3731,
        "Wien Energie GmbH",
        4,
        2,
        22m,
        DateTimeOffset.Parse("2026-10-14T08:15:00Z", CultureInfo.InvariantCulture),
        Availability.Available,
        new LocationFacilities(true, false, true, 2, "https://www.wienenergie.at/privat/produkte/e-mobilitaet/"),
        22m,
        1,
        0);

    public static IEndpointRouteBuilder MapLocationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var locations = endpoints.MapGroup("/api/v1/locations").WithTags("Locations");

        locations.MapGet("/", ListLocationsAsync)
            .WithName("listLocations")
            .WithSummary("List charging locations in a bounding box")
            .WithDescription(
                "Returns the active charging locations inside `bbox` (`minLon,minLat,maxLon,maxLat`, WGS 84), nearest " +
                "to the centre of the box first, at most `limit` (1–2000, default 500); `meta.truncated` tells whether " +
                "more exist. Each location carries its EVSE counts: `evsesAvailable` counts EVSEs whose current status " +
                "is AVAILABLE and not older than the setting `map.status_max_age_minutes`; `availability` summarises " +
                "them as AVAILABLE, OCCUPIED, OUT_OF_ORDER or UNKNOWN. `facilities` tells green energy (100 % renewable " +
                "as stated by the operator), free and roofed parking, the number of barrier-free parking spaces and the " +
                "operator's price page (`priceUrl`); each is null if no source knows it. `maxAvailablePowerKw` is the highest " +
                "power among the EVSEs that are AVAILABLE now (null if none is), e.g. to colour free locations by power. " +
                "`parkId` groups locations of one operator next to each other (setting `location.park_radius_meters`, default " +
                "25 m) into a charging park, e.g. two charging stations a source lists separately at one spot; " +
                "`parkLocations` is the number of locations in it (1 and `parkId` null for a location alone). " +
                "`meta.attribution` lists the sources of the " +
                "data and the attribution text that must be shown with it. Public, no credentials needed.\n\n" +
                "Optional filters (combined with AND; counts of a matching location still cover all its EVSEs):\n" +
                "- `connector`: OCPI connector standard, repeatable or comma-separated, one of them is enough, e.g. " +
                "`connector=IEC_62196_T2_COMBO&connector=CHADEMO` (values as in `getLocation`, case-insensitive).\n" +
                "- `minPowerKw`, `maxPowerKw`: power range of an EVSE in kW (numbers above 0, at most 1000, min not above max), " +
                "e.g. `minPowerKw=50&maxPowerKw=150`.\n" +
                "- `operator`: part of the operator name, case-insensitive, at most 100 characters, e.g. `operator=wien energie`.\n" +
                "- `operatorId`: operator ids from `listOperators`, repeatable, one of them is enough.\n" +
                "- `availableOnly=true`: only locations with at least one AVAILABLE EVSE (`evsesAvailable` > 0).\n" +
                "- `adHoc=true`: an EVSE can be paid without contract (QR_CODE, WEBSITE, CREDIT_CARD_PAYABLE, DEBIT_CARD_PAYABLE, " +
                "CONTACTLESS_CARD_SUPPORT, CHIP_CARD_SUPPORT, PED_TERMINAL or CASH in `authMethods`).\n" +
                "- `roaming=true`: an EVSE accepts charging cards of other providers.\n" +
                "- `greenEnergy=true`: the location states 100 % renewable energy.\n" +
                "- `barrierFree=true`: the location has barrier-free parking spaces.\n" +
                "`connector`, `minPowerKw`, `maxPowerKw`, `adHoc` and `roaming` must hold for the same EVSE: `connector=IEC_62196_T2&minPowerKw=150` " +
                "finds locations with a Type 2 EVSE of at least 150 kW, not a 22 kW Type 2 next to a 150 kW CCS charger. Flags take " +
                "`true` or `false` (`false` = no filter). 422 with field errors for unknown connectors, invalid numbers or flags.")
            .Produces<LocationsResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(
                StatusCodes.Status200OK,
                new LocationsResponse([ExampleLocation], new LocationsMeta(1, DefaultLimit, false, [ExampleAttribution])));

        locations.MapGet("/{id:guid}", GetLocationAsync)
            .WithName("getLocation")
            .WithSummary("Get a charging location with its EVSEs")
            .WithDescription(
                "Returns one active location (`id` from `listLocations`) with all its EVSEs: EVSE-ID (`hasEvseId` false " +
                "for an ID assigned by chargeglobe), current OCPI status with `statusSince`, `statusObservedAt` and " +
                "`statusSource` (key of the source that reported it, e.g. econtrol; the status is the source's own, faults " +
                "included; null and status UNKNOWN if older than `map.status_max_age_minutes`), maximum power, current type, connectors, " +
                "`authMethods` (how a session is started and paid: RFID_READER, APP, QR_CODE, WEBSITE, CHARGING_CONTRACT, " +
                "CREDIT_CARD_PAYABLE, DEBIT_CARD_PAYABLE, CONTACTLESS_CARD_SUPPORT, CHIP_CARD_SUPPORT, PED_TERMINAL, CASH; " +
                "empty if unknown) and `roaming` (charging cards of other providers accepted; null if unknown). " +
                "The location carries `facilities`, `parkId` and `parkLocations` as in `listLocations`; `park` lists the " +
                "other locations of its charging park with their counts, nearest first (empty if it stands alone), so a " +
                "client can show the whole park. Opening a location asks the crawler for a fresh " +
                "status of it (served with the next free request, at most every two minutes). " +
                "`meta.attribution` names the sources. 404 for an unknown or inactive location. Public.")
            .Produces<LocationDetailResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(
                StatusCodes.Status200OK,
                new LocationDetailResponse(
                    new LocationDetailData(
                        ExampleLocation with { EvsesTotal = 1, EvsesAvailable = 1, ParkId = ExampleLocation.Id, ParkLocations = 2 },
                        [
                            new EvseDetail(
                                Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4f"),
                                "AT*VIE*E1234*1",
                                true,
                                "AVAILABLE",
                                DateTimeOffset.Parse("2026-10-14T07:40:00Z", CultureInfo.InvariantCulture),
                                DateTimeOffset.Parse("2026-10-14T08:15:00Z", CultureInfo.InvariantCulture),
                                "econtrol",
                                22m,
                                "AC_3_PHASE",
                                [new ConnectorDetail("IEC_62196_T2", "SOCKET", "AC_3_PHASE", 22m)],
                                ["RFID_READER", "APP", "CREDIT_CARD_PAYABLE"],
                                true),
                        ],
                        [
                            ExampleLocation with
                            {
                                Id = Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c50"),
                                ParkId = ExampleLocation.Id,
                                ParkLocations = 2,
                            },
                        ]),
                    new AttributionMeta([ExampleAttribution])));

        return endpoints;
    }

    private static async Task<IResult> ListLocationsAsync(
        string? bbox,
        int? limit,
        string[]? connector,
        string? minPowerKw,
        string? maxPowerKw,
        string? @operator,
        string[]? operatorId,
        string? availableOnly,
        string? adHoc,
        string? roaming,
        string? greenEnergy,
        string? barrierFree,
        ILocationQueries queries,
        CancellationToken cancellationToken)
    {
        var box = ParseBoundingBox(bbox);
        var take = limit ?? DefaultLimit;
        var connectors = ParseConnectors(connector);
        var power = ParsePower(minPowerKw);
        var maxPower = ParsePower(maxPowerKw);
        var operatorIds = ParseIds(operatorId);
        var operatorPart = string.IsNullOrWhiteSpace(@operator) ? null : @operator.Trim();
        var flags = new Dictionary<string, bool?>
        {
            ["availableOnly"] = ParseFlag(availableOnly),
            ["adHoc"] = ParseFlag(adHoc),
            ["roaming"] = ParseFlag(roaming),
            ["greenEnergy"] = ParseFlag(greenEnergy),
            ["barrierFree"] = ParseFlag(barrierFree),
        };
        var validation = new Validation()
            .Require(box is not null, "bbox", "Must be minLon,minLat,maxLon,maxLat in WGS 84 with min below max, e.g. 16.2,48.1,16.6,48.3.")
            .Require(take is >= 1 and <= MaxLimit, "limit", $"Must be between 1 and {MaxLimit}.")
            .Require(connectors is not null, "connector", "Must be OCPI connector standards such as IEC_62196_T2, IEC_62196_T2_COMBO or CHADEMO.")
            .Require(power is null or (> 0 and <= MaxPowerKw), "minPowerKw", $"Must be a number above 0 and at most {MaxPowerKw}.")
            .Require(maxPower is null or (> 0 and <= MaxPowerKw), "maxPowerKw", $"Must be a number above 0 and at most {MaxPowerKw}.")
            .Require(power is null || maxPower is null || power <= maxPower, "maxPowerKw", "Must not be below minPowerKw.")
            .Require(operatorIds is not null, "operatorId", "Must be operator ids (UUIDs) from listOperators.")
            .Require(operatorPart is not { Length: > MaxOperatorLength }, "operator", $"Must have at most {MaxOperatorLength} characters.");
        foreach (var (name, value) in flags)
        {
            validation.Require(value is not null, name, "Must be true or false.");
        }

        if (validation.ToResult() is { } invalid)
        {
            return invalid;
        }

        var filter = new LocationFilter(
            connectors!, power, maxPower, operatorPart, operatorIds!, flags["availableOnly"]!.Value, flags["adHoc"]!.Value, flags["roaming"]!.Value,
            flags["greenEnergy"]!.Value, flags["barrierFree"]!.Value);
        var page = await queries.InBoundingBoxAsync(box!, filter, take, cancellationToken);
        return Results.Ok(new LocationsResponse(page.Locations, new LocationsMeta(page.Locations.Count, take, page.Truncated, page.Attribution)));
    }

    private static async Task<IResult> GetLocationAsync(
        Guid id, ILocationQueries queries, IStatusDemand demand, CancellationToken cancellationToken)
    {
        if (await queries.FindAsync(id, cancellationToken) is not { } detail)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Not found",
                detail: $"There is no active location with the id '{id}'. Use listLocations to find locations.");
        }

        // Someone looks at the location: its query point is read with priority (at most every two minutes).
        await demand.RequestAsync(id, cancellationToken);
        return Results.Ok(new LocationDetailResponse(new LocationDetailData(detail.Location, detail.Evses, detail.Park), new AttributionMeta(detail.Attribution)));
    }

    /// <summary>OCPI connector standards (repeated and/or comma-separated, any case); null if one is unknown.</summary>
    public static IReadOnlyList<string>? ParseConnectors(string[]? values)
    {
        var standards = new List<string>();
        foreach (var part in (values ?? []).SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            // Enum.TryParse also takes numbers; only names count.
            if (!Enum.TryParse<ConnectorStandard>(part, ignoreCase: true, out var standard) || char.IsDigit(part[0]))
            {
                return null;
            }

            standards.Add(standard.ToString());
        }

        return [.. standards.Distinct()];
    }

    /// <summary>UUIDs (repeated and/or comma-separated); null if one is not a UUID.</summary>
    public static IReadOnlyList<Guid>? ParseIds(string[]? values)
    {
        var ids = new List<Guid>();
        foreach (var part in (values ?? []).SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (!Guid.TryParse(part, out var id))
            {
                return null;
            }

            ids.Add(id);
        }

        return [.. ids.Distinct()];
    }

    /// <summary>A power filter: null if missing, -1 if not a number (rejected by the validation).</summary>
    public static decimal? ParsePower(string? value) =>
        value is null ? null : decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var power) ? power : -1;

    /// <summary>false if missing (no filter), null if neither true nor false.</summary>
    public static bool? ParseFlag(string? value) =>
        value is null ? false : bool.TryParse(value, out var flag) ? flag : null;

    /// <summary>The box of the query parameter; null if it is missing or invalid.</summary>
    public static BoundingBox? ParseBoundingBox(string? value)
    {
        var parts = (value ?? string.Empty).Split(',');
        var numbers = parts
            .Select(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : double.NaN)
            .ToArray();
        if (numbers.Length != 4 || numbers.Any(double.IsNaN))
        {
            return null;
        }

        try
        {
            return new BoundingBox(numbers[0], numbers[1], numbers[2], numbers[3]);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
