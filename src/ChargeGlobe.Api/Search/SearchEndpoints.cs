using ChargeGlobe.Api.Auth;
using ChargeGlobe.Api.OpenApi;
using ChargeGlobe.Application.Locations;

namespace ChargeGlobe.Api.Search;

/// <param name="Returned">Number of hits in <c>data</c>.</param>
public sealed record SearchMeta(int Returned, int Limit);

public sealed record SearchResponse(IReadOnlyList<SearchHit> Data, SearchMeta Meta);

/// <summary>Search over charge points and locations for the map (docs/06, "Suche").</summary>
public static class SearchEndpoints
{
    private const int DefaultLimit = 10;
    private const int MaxLimit = 50;
    private const int MinQueryLength = 2;
    private const int MaxQueryLength = 100;

    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/search", SearchAsync)
            .WithTags("Locations")
            .WithName("search")
            .WithSummary("Search charge points by EVSE-ID and locations by name or address")
            .WithDescription(
                "Searches the active charge points and locations for `q` (2–100 characters): first EVSEs whose EVSE-ID starts " +
                "with `q` in any notation (`AT*VIE*E1234`, `AT-VIE-E1234`, `atviee1234`), then locations whose name, street, " +
                "postal code and city contain every word of `q` (case-insensitive), the most similar first; at most `limit` " +
                "hits (1–50, default 10). Each hit has `type` (EVSE or LOCATION), `label`, `detail` (address), `locationId` " +
                "for `getLocation` and the coordinates to center the map. 422 for a missing or too short `q` or a limit out " +
                "of range. Public, no credentials needed.")
            .Produces<SearchResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(
                StatusCodes.Status200OK,
                new SearchResponse(
                    [
                        new SearchHit(
                            "LOCATION",
                            "Parkgarage Stephansplatz",
                            "Stephansplatz 1, 1010 Wien",
                            Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4e"),
                            48.2085,
                            16.3731,
                            null),
                    ],
                    new SearchMeta(1, DefaultLimit)));
        return endpoints;
    }

    private static async Task<IResult> SearchAsync(string? q, int? limit, ISearchQueries search, CancellationToken cancellationToken)
    {
        var text = q?.Trim() ?? string.Empty;
        var take = limit ?? DefaultLimit;
        var invalid = new Validation()
            .Require(text.Length is >= MinQueryLength and <= MaxQueryLength, "q", $"Must have {MinQueryLength} to {MaxQueryLength} characters.")
            .Require(take is >= 1 and <= MaxLimit, "limit", $"Must be between 1 and {MaxLimit}.")
            .ToResult();
        if (invalid is not null)
        {
            return invalid;
        }

        var hits = await search.SearchAsync(text, take, cancellationToken);
        return Results.Ok(new SearchResponse(hits, new SearchMeta(hits.Count, take)));
    }
}
