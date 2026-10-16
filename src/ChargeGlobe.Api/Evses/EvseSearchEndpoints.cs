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
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Identity;

namespace ChargeGlobe.Api.Evses;

/// <param name="Returned">Number of EVSEs in <c>data</c>.</param>
/// <param name="NextCursor">Pass as <c>cursor</c> for the next page; null on the last page.</param>
public sealed record EvsesMeta(int Returned, int Limit, string? NextCursor, IReadOnlyList<SourceAttribution> Attribution);

public sealed record EvsesResponse(IReadOnlyList<EvseListItem> Data, EvsesMeta Meta);

/// <summary>Search over the EVSEs with filters and cursor paging (docs/06, "Ladepunkte").</summary>
public static partial class EvseSearchEndpoints
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 1000;

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
                "`AT*MER*E006566`). `sort`: `evseId` (default, by normalized EVSE-ID) or `statusSince` (longest unchanged status " +
                "first, EVSEs without status last). Paging: at most `limit` (1–1000, default 100) per page; pass " +
                "`meta.nextCursor` as `cursor` with the same filters and sort for the next page (null on the last page). Each " +
                "EVSE has `id`, `evseId`, `location` (`id`, `name`, `city`, `region`, `parkId`), `chargingStation` (`id`, " +
                "`label`; null if the source names none), `status` as stored (not replaced when stale), `statusSince`, " +
                "`statusSource` and `maxPowerKw`. Details of one EVSE: `getEvse`. 422 for invalid filters, an unknown region " +
                "or a cursor of another sort. `meta.attribution` names the sources of the EVSEs on the page.")
            .Produces<EvsesResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(
                StatusCodes.Status200OK,
                new EvsesResponse(
                    [
                        new EvseListItem(
                            Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4f"),
                            "AT*EVN*E2453*1",
                            new EvseListLocation(Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4e"), "Rathausplatz", "St. Pölten", "AT-3", Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4e")),
                            new EvseStationRef(Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c41"), "AT*EVN*E2453"),
                            "OUTOFORDER",
                            DateTimeOffset.Parse("2026-10-14T06:12:00Z", CultureInfo.InvariantCulture),
                            "econtrol",
                            22m),
                    ],
                    new EvsesMeta(1, DefaultLimit, null, [LocationsEndpoints.ExampleAttribution])));
        return endpoints;
    }

    private static async Task<IResult> ListEvsesAsync(
        string? operatorId,
        string? region,
        string[]? status,
        string? minPowerKw,
        string? maxPowerKw,
        string[]? connector,
        string? statusOlderThan,
        string? chargingStation,
        string? sort,
        string? cursor,
        int? limit,
        IEvseQueries queries,
        IStatsQueries stats,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var take = limit ?? DefaultLimit;
        var (country, party) = ParseOperator(operatorId);
        var code = string.IsNullOrWhiteSpace(region) ? null : region.Trim().ToUpperInvariant();
        var statuses = ParseStatuses(status);
        var connectors = LocationsEndpoints.ParseConnectors(connector);
        var power = LocationsEndpoints.ParsePower(minPowerKw);
        var maxPower = LocationsEndpoints.ParsePower(maxPowerKw);
        var age = ParseDuration(statusOlderThan);
        EvseSort? order = sort is null ? EvseSort.EvseId
            : string.Equals(sort, "evseId", StringComparison.OrdinalIgnoreCase) ? EvseSort.EvseId
            : string.Equals(sort, "statusSince", StringComparison.OrdinalIgnoreCase) ? EvseSort.StatusSince
            : null;
        var after = cursor is null || order is null ? null : DecodeCursor(cursor, order.Value);
        var invalid = new Validation()
            .Require(take is >= 1 and <= MaxLimit, "limit", $"Must be between 1 and {MaxLimit}.")
            .Require(operatorId is null || party is not null, "operatorId", "Must be the operator part of an EVSE-ID such as EVN, or with country, AT*EVN.")
            .Require(code is null || (RegionCode().IsMatch(code) && await stats.RegionExistsAsync(code, cancellationToken)), "region", "Must be the ISO 3166-2 code of a known region, e.g. AT-3.")
            .Require(statuses is not null, "status", "Must be OCPI statuses such as AVAILABLE, CHARGING, OUTOFORDER, INOPERATIVE or UNKNOWN.")
            .Require(connectors is not null, "connector", "Must be OCPI connector standards such as IEC_62196_T2, IEC_62196_T2_COMBO or CHADEMO.")
            .Require(power is null or (> 0 and <= LocationsEndpoints.MaxPowerKw), "minPowerKw", $"Must be a number above 0 and at most {LocationsEndpoints.MaxPowerKw}.")
            .Require(maxPower is null or (> 0 and <= LocationsEndpoints.MaxPowerKw), "maxPowerKw", $"Must be a number above 0 and at most {LocationsEndpoints.MaxPowerKw}.")
            .Require(power is null || maxPower is null || power <= maxPower, "maxPowerKw", "Must not be below minPowerKw.")
            .Require(statusOlderThan is null || (age > TimeSpan.Zero && age <= MaxStatusAge), "statusOlderThan", "Must be an ISO 8601 duration above zero and at most a year, e.g. PT24H or P7D.")
            .Require(chargingStation is null || !string.IsNullOrWhiteSpace(chargingStation), "chargingStation", "Must be the id or label of a charging station.")
            .Require(order is not null, "sort", "Must be evseId or statusSince.")
            .Require(cursor is null || order is null || after is not null, "cursor", "Must be meta.nextCursor of a previous page with the same sort.")
            .ToResult();
        if (invalid is not null)
        {
            return invalid;
        }

        var filter = new EvseSearchFilter(
            country, party, code, statuses!, power, maxPower, connectors!, age is { } span ? timeProvider.GetUtcNow() - span : null, chargingStation?.Trim());
        var page = await queries.SearchAsync(filter, order!.Value, after, take, cancellationToken);
        return Results.Ok(new EvsesResponse(page.Evses, new EvsesMeta(page.Evses.Count, take, page.Next is { } next ? EncodeCursor(next) : null, page.Attribution)));
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
                && (sort == EvseSort.EvseId || DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
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
