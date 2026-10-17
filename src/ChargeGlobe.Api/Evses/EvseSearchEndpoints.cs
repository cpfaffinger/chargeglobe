using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using ChargeGlobe.Api.Auth;
using ChargeGlobe.Api.Locations;
using ChargeGlobe.Api.OpenApi;
using ChargeGlobe.Application.Evses;
using ChargeGlobe.Application.Locations;
using ChargeGlobe.Application.Stats;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Identity;

namespace ChargeGlobe.Api.Evses;

/// <param name="Returned">Number of EVSEs in <c>data</c>.</param>
/// <param name="NextCursor">Pass as <c>cursor</c> for the next page; null on the last page.</param>
public sealed record EvsesMeta(int Returned, int Limit, string? NextCursor, IReadOnlyList<SourceAttribution> Attribution);

public sealed record EvsesResponse(IReadOnlyList<EvseListItem> Data, EvsesMeta Meta);

/// <summary>Query parameters of <c>listEvses</c> and <c>exportEvses</c>.</summary>
public sealed record EvseSearchQuery(
    string? OperatorId,
    string? Region,
    string[]? Status,
    string? MinPowerKw,
    string? MaxPowerKw,
    string[]? Connector,
    string? StatusOlderThan,
    string? ChargingStation,
    string? Bbox,
    string? Near,
    int? RadiusMeters,
    string? Sort,
    string? Cursor,
    int? Limit);

/// <summary>Search over the EVSEs with filters and cursor paging (docs/06, "Ladepunkte").</summary>
public static partial class EvseSearchEndpoints
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 1000;

    /// <summary>Most rows of one export.</summary>
    public const int MaxExportRows = 100_000;

    /// <summary>Largest radius around <c>near</c>.</summary>
    public const int MaxRadiusMeters = 50_000;

    /// <summary>Radius around <c>near</c> without <c>radiusMeters</c>.</summary>
    public const int DefaultRadiusMeters = 5_000;

    /// <summary>Longest <c>statusOlderThan</c>.</summary>
    public static readonly TimeSpan MaxStatusAge = TimeSpan.FromDays(365);

    public static IEndpointRouteBuilder MapEvseSearchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/evses", ListEvsesAsync)
            .WithTags("EVSEs")
            .RequirePermission(Permissions.EvseRead)
            .WithName("listEvses")
            .WithSummary("Search EVSEs (charge points) by operator, region, status, power, connector and status age")
            .WithDescription(
                "Lists the active EVSEs of active locations that match all given filters. Filters: `operatorId` (operator part " +
                "of the EVSE-ID, `EVN`, or with country, `AT*EVN`), `region` (ISO 3166-2 code, e.g. `AT-3`), `status` (current " +
                "OCPI status, repeated or comma-separated, e.g. `OUTOFORDER,INOPERATIVE`; `UNKNOWN` also matches EVSEs without " +
                "status), `minPowerKw`/`maxPowerKw`, `connector` (OCPI standards as in `listLocations`), `statusOlderThan` " +
                "(ISO 8601 duration: the current status began at least this long ago, e.g. `PT24H` – together with " +
                "`status=OUTOFORDER` \"out of order for over 24 hours\"), `chargingStation` (id or label of the station, e.g. " +
                "`AT*MER*E006566`), `bbox` (minLon,minLat,maxLon,maxLat in WGS 84) or `near` (lat,lon) with `radiusMeters` (1–50000, " +
                "default 5000; each EVSE then carries `distanceMeters`). `sort`: `evseId` (default, by normalized EVSE-ID), " +
                "`statusSince` (longest unchanged status first, EVSEs without status last) or `distance` (nearest first, needs " +
                "`near`). The whole result as CSV: `exportEvses`. Paging: at most `limit` (1–1000, default 100) per page; pass " +
                "`meta.nextCursor` as `cursor` with the same filters and sort for the next page (null on the last page). Each " +
                "EVSE has `id`, `evseId`, `location` (`id`, `name`, `city`, `region`, `parkId`, `latitude`, `longitude`), `chargingStation` (`id`, " +
                "`label`; null if the source names none), `status` as stored (not replaced when stale), `statusSince`, " +
                "`statusSource`, `maxPowerKw` and `distanceMeters` (null without `near`). Details of one EVSE: `getEvse`. 422 for " +
                "invalid filters, an unknown region, `bbox` together with `near`, `sort=distance` without `near` or a cursor of " +
                "another sort. `meta.attribution` names the sources of the EVSEs on the page.")
            .Produces<EvsesResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(
                StatusCodes.Status200OK,
                new EvsesResponse(
                    [
                        new EvseListItem(
                            Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4f"),
                            "AT*EVN*E2453*1",
                            new EvseListLocation(Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4e"), "Rathausplatz", "St. Pölten", "AT-3", Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4e"), 48.2047, 15.6256),
                            new EvseStationRef(Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c41"), "AT*EVN*E2453"),
                            "OUTOFORDER",
                            DateTimeOffset.Parse("2026-10-14T06:12:00Z", CultureInfo.InvariantCulture),
                            "econtrol",
                            22m,
                            1240.5),
                    ],
                    new EvsesMeta(1, DefaultLimit, null, [LocationsEndpoints.ExampleAttribution])));

        endpoints.MapGet("/api/v1/evses/export", ExportEvsesAsync)
            .WithTags("EVSEs")
            .RequirePermission(Permissions.ExportRead)
            .WithName("exportEvses")
            .WithSummary("Export the EVSEs of a search as CSV")
            .WithDescription(
                "Streams the EVSEs that match the filters of `listEvses` (same parameters and `sort`, no `cursor`) as CSV " +
                "(RFC 4180, UTF-8, comma-separated), at most `limit` rows (1–100000, default 100000). The first line is a " +
                "comment `# Sources: …` with the attribution of the sources, the second the header: evse_id, location_id, " +
                "location_name, city, region, park_id, latitude, longitude, charging_station, status, status_since, " +
                "status_source, max_power_kw, distance_m. Times are ISO 8601 in UTC, numbers use a dot. 422 for invalid " +
                "filters as in `listEvses`.")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);
        return endpoints;
    }

    private static async Task<IResult> ListEvsesAsync(
        [AsParameters] EvseSearchQuery query, IEvseQueries queries, IStatsQueries stats, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var (request, invalid) = await ParseAsync(query, DefaultLimit, MaxLimit, stats, timeProvider, cancellationToken);
        if (invalid is not null)
        {
            return invalid;
        }

        var page = await queries.SearchAsync(request!.Filter, request.Sort, request.After, request.Limit, cancellationToken);
        return Results.Ok(new EvsesResponse(page.Evses, new EvsesMeta(page.Evses.Count, request.Limit, page.Next is { } next ? EncodeCursor(next) : null, page.Attribution)));
    }

    private static async Task<IResult> ExportEvsesAsync(
        [AsParameters] EvseSearchQuery query, IEvseQueries queries, IStatsQueries stats, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var (request, invalid) = await ParseAsync(query with { Cursor = null }, MaxExportRows, MaxExportRows, stats, timeProvider, cancellationToken);
        if (invalid is not null)
        {
            return invalid;
        }

        var sources = await queries.EvseSourcesAsync(cancellationToken);
        return Results.Stream(
            async stream =>
            {
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 64 * 1024);
                await writer.WriteLineAsync($"# Sources: {string.Join(" | ", sources.Select(source => source.Text))}");
                await writer.WriteLineAsync(
                    "evse_id,location_id,location_name,city,region,park_id,latitude,longitude,charging_station,status,status_since,status_source,max_power_kw,distance_m");
                await foreach (var evse in queries.ExportAsync(request!.Filter, request.Sort, request.Limit, cancellationToken))
                {
                    await writer.WriteLineAsync(CsvLine(evse));
                }
            },
            "text/csv; charset=utf-8",
            "evses.csv");
    }

    /// <summary>One CSV row of an EVSE (RFC 4180 quoting, invariant numbers, ISO 8601 times).</summary>
    public static string CsvLine(EvseListItem evse)
    {
        static string Field(string? value) =>
            value is null ? string.Empty
            : value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
        static string Number(double? value) => value?.ToString("0.#######", CultureInfo.InvariantCulture) ?? string.Empty;
        return string.Join(',', [
            Field(evse.EvseId),
            evse.Location.Id.ToString(),
            Field(evse.Location.Name),
            Field(evse.Location.City),
            Field(evse.Location.Region),
            evse.Location.ParkId?.ToString() ?? string.Empty,
            Number(evse.Location.Latitude),
            Number(evse.Location.Longitude),
            Field(evse.ChargingStation?.Label),
            evse.Status,
            evse.StatusSince?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) ?? string.Empty,
            Field(evse.StatusSource),
            evse.MaxPowerKw?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty,
            evse.DistanceMeters is { } distance ? Math.Round(distance).ToString(CultureInfo.InvariantCulture) : string.Empty,
        ]);
    }

    private sealed record SearchRequest(EvseSearchFilter Filter, EvseSort Sort, EvseCursor? After, int Limit);

    /// <summary>The filter, sort, cursor and limit of the query; a 422 problem if one of them is invalid.</summary>
    private static async Task<(SearchRequest? Request, IResult? Invalid)> ParseAsync(
        EvseSearchQuery query, int defaultLimit, int maxLimit, IStatsQueries stats, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var take = query.Limit ?? defaultLimit;
        var (country, party) = ParseOperator(query.OperatorId);
        var code = string.IsNullOrWhiteSpace(query.Region) ? null : query.Region.Trim().ToUpperInvariant();
        var statuses = ParseStatuses(query.Status);
        var connectors = LocationsEndpoints.ParseConnectors(query.Connector);
        var power = LocationsEndpoints.ParsePower(query.MinPowerKw);
        var maxPower = LocationsEndpoints.ParsePower(query.MaxPowerKw);
        var age = ParseDuration(query.StatusOlderThan);
        var box = query.Bbox is null ? null : LocationsEndpoints.ParseBoundingBox(query.Bbox);
        var near = query.Near is null ? null : ParsePoint(query.Near);
        var radius = query.RadiusMeters ?? DefaultRadiusMeters;
        EvseSort? order = query.Sort is null ? EvseSort.EvseId
            : string.Equals(query.Sort, "evseId", StringComparison.OrdinalIgnoreCase) ? EvseSort.EvseId
            : string.Equals(query.Sort, "statusSince", StringComparison.OrdinalIgnoreCase) ? EvseSort.StatusSince
            : string.Equals(query.Sort, "distance", StringComparison.OrdinalIgnoreCase) ? EvseSort.Distance
            : null;
        var after = query.Cursor is null || order is null ? null : DecodeCursor(query.Cursor, order.Value);
        var invalid = new Validation()
            .Require(take >= 1 && take <= maxLimit, "limit", $"Must be between 1 and {maxLimit}.")
            .Require(query.OperatorId is null || party is not null, "operatorId", "Must be the operator part of an EVSE-ID such as EVN, or with country, AT*EVN.")
            .Require(code is null || (RegionCode().IsMatch(code) && await stats.RegionExistsAsync(code, cancellationToken)), "region", "Must be the ISO 3166-2 code of a known region, e.g. AT-3.")
            .Require(statuses is not null, "status", "Must be OCPI statuses such as AVAILABLE, CHARGING, OUTOFORDER, INOPERATIVE or UNKNOWN.")
            .Require(connectors is not null, "connector", "Must be OCPI connector standards such as IEC_62196_T2, IEC_62196_T2_COMBO or CHADEMO.")
            .Require(power is null or (> 0 and <= LocationsEndpoints.MaxPowerKw), "minPowerKw", $"Must be a number above 0 and at most {LocationsEndpoints.MaxPowerKw}.")
            .Require(maxPower is null or (> 0 and <= LocationsEndpoints.MaxPowerKw), "maxPowerKw", $"Must be a number above 0 and at most {LocationsEndpoints.MaxPowerKw}.")
            .Require(power is null || maxPower is null || power <= maxPower, "maxPowerKw", "Must not be below minPowerKw.")
            .Require(query.StatusOlderThan is null || (age > TimeSpan.Zero && age <= MaxStatusAge), "statusOlderThan", "Must be an ISO 8601 duration above zero and at most a year, e.g. PT24H or P7D.")
            .Require(query.ChargingStation is null || !string.IsNullOrWhiteSpace(query.ChargingStation), "chargingStation", "Must be the id or label of a charging station.")
            .Require(query.Bbox is null || box is not null, "bbox", "Must be minLon,minLat,maxLon,maxLat in WGS 84 with min below max, e.g. 15.5,48.1,15.8,48.3.")
            .Require(query.Near is null || near is not null, "near", "Must be lat,lon in WGS 84, e.g. 48.2047,15.6256.")
            .Require(query.Bbox is null || query.Near is null, "near", "Use either bbox or near, not both.")
            .Require(radius is >= 1 and <= MaxRadiusMeters, "radiusMeters", $"Must be between 1 and {MaxRadiusMeters}.")
            .Require(order is not null, "sort", "Must be evseId, statusSince or distance.")
            .Require(order is not EvseSort.Distance || query.Near is not null, "sort", "Sorting by distance needs near.")
            .Require(query.Cursor is null || order is null || after is not null, "cursor", "Must be meta.nextCursor of a previous page with the same sort.")
            .ToResult();
        if (invalid is not null)
        {
            return (null, invalid);
        }

        var filter = new EvseSearchFilter(
            country, party, code, statuses!, power, maxPower, connectors!, age is { } span ? timeProvider.GetUtcNow() - span : null,
            query.ChargingStation?.Trim(), box, near, near is null ? 0 : radius);
        return (new SearchRequest(filter, order!.Value, after, take), null);
    }

    /// <summary>A point of the form <c>lat,lon</c>; null if it is invalid.</summary>
    public static GeoPoint? ParsePoint(string value)
    {
        var parts = value.Split(',');
        try
        {
            return parts.Length == 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude)
                ? new GeoPoint(latitude, longitude)
                : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Country (or null) and operator part of <c>EVN</c>, <c>AT*EVN</c>, <c>AT-EVN</c> or <c>ATEVN</c>; party null if invalid.</summary>
    public static (string? Country, string? Party) ParseOperator(string? value)
    {
        var compact = string.Concat((value ?? string.Empty).ToUpperInvariant().Where(character => character is not ('*' or '-') && !char.IsWhiteSpace(character)));
        if (!compact.All(char.IsAsciiLetterOrDigit))
        {
            return (null, null);
        }

        return compact.Length switch
        {
            3 => (null, compact),
            5 when char.IsAsciiLetter(compact[0]) && char.IsAsciiLetter(compact[1]) => (compact[..2], compact[2..]),
            _ => (null, null),
        };
    }

    /// <summary>OCPI statuses (repeated and/or comma-separated, any case); null if one is unknown.</summary>
    public static IReadOnlyList<string>? ParseStatuses(string[]? values)
    {
        var statuses = new List<string>();
        foreach (var part in (values ?? []).SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            // Enum.TryParse also takes numbers; only names count.
            if (!Enum.TryParse<EvseStatus>(part, ignoreCase: true, out var parsed) || char.IsDigit(part[0]))
            {
                return null;
            }

            statuses.Add(parsed.ToString());
        }

        return [.. statuses.Distinct()];
    }

    /// <summary>An ISO 8601 duration; <see cref="TimeSpan.Zero"/> if invalid (rejected by the validation), null if missing.</summary>
    public static TimeSpan? ParseDuration(string? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return XmlConvert.ToTimeSpan(value.Trim());
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            return TimeSpan.Zero;
        }
    }

    /// <summary>The cursor as URL-safe Base64 of "sort|key|id".</summary>
    public static string EncodeCursor(EvseCursor cursor) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cursor.Sort}|{cursor.Key}|{cursor.Id}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The cursor of <see cref="EncodeCursor"/>; null if it is malformed or of another sort.</summary>
    public static EvseCursor? DecodeCursor(string value, EvseSort sort)
    {
        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=')));
            var parts = text.Split('|');
            return parts.Length == 3
                && Enum.TryParse<EvseSort>(parts[0], out var kind) && kind == sort
                && Guid.TryParse(parts[2], out var id)
                && parts[1].Length > 0
                && sort switch
                {
                    EvseSort.EvseId => true,
                    EvseSort.StatusSince => DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
                    _ => double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _),
                }
                ? new EvseCursor(sort, parts[1], id)
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    [GeneratedRegex("^[A-Z]{2}-[A-Z0-9]{1,3}$")]
    private static partial Regex RegionCode();
}
