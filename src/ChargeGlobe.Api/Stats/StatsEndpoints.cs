using System.Text.RegularExpressions;
using ChargeGlobe.Api.Auth;
using ChargeGlobe.Api.Locations;
using ChargeGlobe.Api.OpenApi;
using ChargeGlobe.Application.Locations;
using ChargeGlobe.Application.Stats;
using ChargeGlobe.Domain.Identity;
using Microsoft.Extensions.Caching.Memory;

namespace ChargeGlobe.Api.Stats;

/// <param name="Region">The region asked for; null for all.</param>
public sealed record StatsData(
    string? Region,
    StatsTotals Totals,
    IReadOnlyList<RegionCount> ByRegion,
    IReadOnlyList<PowerClassCount> ByPowerClass,
    IReadOnlyList<ConnectorCount> ByConnector,
    IReadOnlyList<OperatorCount> TopOperators,
    StatusCounts Status,
    Freshness Freshness,
    DateTimeOffset GeneratedAt);

public sealed record StatsResponse(StatsData Data, AttributionMeta Meta);

/// <summary>Counts of the charging infrastructure (docs/06, "Stammdaten &amp; Meta").</summary>
public static partial class StatsEndpoints
{
    /// <summary>The counts change slowly; one calculation serves all requests of a minute.</summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    public static IEndpointRouteBuilder MapStatsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/stats", GetStatsAsync)
            .WithTags("Meta")
            .RequirePermission(Permissions.EvseRead)
            .WithName("getStats")
            .WithSummary("Get counts of the charging infrastructure")
            .WithDescription(
                "Counts the active charging locations and their active EVSEs, in all regions or in `region` (ISO 3166-2 code " +
                "such as `AT-9`; 422 if unknown). `totals`: locations, EVSEs and their connectors. `byRegion`: locations and " +
                "EVSEs per region. `byPowerClass`: EVSEs by maximum power, classes `UP_TO_22`, `FROM_23_TO_49`, " +
                "`FROM_50_TO_149`, `FROM_150` (kW) and `UNKNOWN`, always all five. `byConnector`: connectors per OCPI " +
                "standard, most first. `topOperators`: the 10 operators with the most EVSEs. `status`: EVSEs by current " +
                "status – `available`, `occupied` (charging, blocked, reserved), `faulted` (out of order, inoperative) and " +
                "`unknown` count only statuses newer than the freshness limit of the map, older or missing ones are `stale`; " +
                "the five add up to `totals.evses`. `freshness`: median and 90th percentile of the age of the current " +
                "statuses in minutes (null without any). `generatedAt`: when the counts were made; they are kept for 60 s. " +
                "`meta.attribution` names the sources of the counted locations.")
            .Produces<StatsResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(
                StatusCodes.Status200OK,
                new StatsResponse(
                    new StatsData(
                        null,
                        new StatsTotals(5_840, 13_210, 19_880),
                        [new RegionCount("AT-9", "Wien", 1_620, 3_980), new RegionCount("AT-3", "Niederösterreich", 1_410, 3_050)],
                        [
                            new PowerClassCount("UP_TO_22", 10_120), new PowerClassCount("FROM_23_TO_49", 310), new PowerClassCount("FROM_50_TO_149", 1_480),
                            new PowerClassCount("FROM_150", 1_150), new PowerClassCount("UNKNOWN", 150),
                        ],
                        [new ConnectorCount("IEC_62196_T2", 12_400), new ConnectorCount("IEC_62196_T2_COMBO", 2_930)],
                        [new OperatorCount(Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c40"), "Wien Energie GmbH", 2_310)],
                        new StatusCounts(7_020, 2_180, 410, 90, 3_510),
                        new Freshness(4.5, 12.0),
                        DateTimeOffset.Parse("2026-10-15T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
                    new AttributionMeta([LocationsEndpoints.ExampleAttribution])));
        return endpoints;
    }

    private static async Task<IResult> GetStatsAsync(string? region, IStatsQueries queries, IMemoryCache cache, CancellationToken cancellationToken)
    {
        var code = string.IsNullOrWhiteSpace(region) ? null : region.Trim().ToUpperInvariant();
        if (code is not null && (!RegionCode().IsMatch(code) || !await queries.RegionExistsAsync(code, cancellationToken)))
        {
            return new Validation().Require(false, "region", "Must be the ISO 3166-2 code of a known region, e.g. AT-9.").ToResult()!;
        }

        var stats = (await cache.GetOrCreateAsync($"stats:{code}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            return queries.GetAsync(code, cancellationToken);
        }))!;
        return Results.Ok(new StatsResponse(
            new StatsData(
                code, stats.Totals, stats.ByRegion, stats.ByPowerClass, stats.ByConnector, stats.TopOperators, stats.Status, stats.Freshness, stats.GeneratedAt),
            new AttributionMeta(stats.Attribution)));
    }

    [GeneratedRegex("^[A-Z]{2}-[A-Z0-9]{1,3}$")]
    private static partial Regex RegionCode();
}
