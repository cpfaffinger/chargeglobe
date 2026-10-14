using System.Globalization;
using System.Security.Claims;
using ChargeGlobe.Api.Auth;
using ChargeGlobe.Api.Locations;
using ChargeGlobe.Api.OpenApi;
using ChargeGlobe.Application.Favorites;
using ChargeGlobe.Application.Locations;

namespace ChargeGlobe.Api.Account;

/// <param name="Max">Most favorites one account can have.</param>
public sealed record FavoritesMeta(int Returned, int Max, IReadOnlyList<SourceAttribution> Attribution);

public sealed record FavoritesResponse(IReadOnlyList<Favorite> Data, FavoritesMeta Meta);

/// <summary>Favorite locations of the signed-in user (docs/06, "Favoriten").</summary>
public static class FavoritesEndpoints
{
    public static IEndpointRouteBuilder MapFavoritesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var favorites = endpoints.MapGroup("/api/v1/me/favorites").WithTags("Favorites").RequirePermission(RequirePermissionAttribute.Authenticated);

        favorites.MapGet("/", ListFavoritesAsync)
            .WithName("listFavorites")
            .WithSummary("List the caller's favorite locations with their current status")
            .WithDescription(
                "Returns the active locations the caller marked as favorite, newest first: `data[]` with `location` (as in " +
                "`listLocations`: counts of free EVSEs, `availability`, `maxPowerKw`, `maxAvailablePowerKw`, " +
                "`statusObservedAt`, `facilities`) and `addedAt`. Locations that are no longer active are left out. `meta` " +
                "with `returned`, `max` (most favorites per account) and `attribution`. Meant for refreshing a list of " +
                "favorites every minute. Requires an account or API key, no specific permission.")
            .Produces<FavoritesResponse>()
            .WithResponseExample(
                StatusCodes.Status200OK,
                new FavoritesResponse(
                    [new Favorite(LocationsEndpoints.ExampleLocation, DateTimeOffset.Parse("2026-10-14T08:00:00Z", CultureInfo.InvariantCulture))],
                    new FavoritesMeta(1, IFavorites.Max, [LocationsEndpoints.ExampleAttribution])));

        favorites.MapPut("/{locationId:guid}", AddFavoriteAsync)
            .WithName("addFavorite")
            .WithSummary("Mark a location as favorite")
            .WithDescription(
                "Adds the location (`id` from `listLocations`) to the caller's favorites. Idempotent: 204 also if it is a " +
                "favorite already. 404 for an unknown or inactive location; 409 if the account has the maximum of " +
                "200 favorites. Requires an account or API key, no specific permission.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        favorites.MapDelete("/{locationId:guid}", RemoveFavoriteAsync)
            .WithName("removeFavorite")
            .WithSummary("Remove a location from the favorites")
            .WithDescription(
                "Removes the location from the caller's favorites. Idempotent: 204 also if it was no favorite. Requires an " +
                "account or API key, no specific permission.")
            .Produces(StatusCodes.Status204NoContent);

        return endpoints;
    }

    private static async Task<FavoritesResponse> ListFavoritesAsync(ClaimsPrincipal principal, IFavorites favorites, CancellationToken cancellationToken)
    {
        var list = await favorites.ListAsync(principal.GetActor().UserId!.Value, cancellationToken);
        return new FavoritesResponse(list.Favorites, new FavoritesMeta(list.Favorites.Count, IFavorites.Max, list.Attribution));
    }

    private static async Task<IResult> AddFavoriteAsync(Guid locationId, ClaimsPrincipal principal, IFavorites favorites, CancellationToken cancellationToken)
    {
        var actor = principal.GetActor();
        return await favorites.AddAsync(actor.UserId!.Value, locationId, actor, cancellationToken) switch
        {
            FavoriteOutcome.UnknownLocation => Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Not found",
                detail: $"There is no active location with the id '{locationId}'. Use listLocations to find locations."),
            FavoriteOutcome.LimitReached => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Conflict",
                detail: $"An account can have at most {IFavorites.Max} favorites. Remove one first."),
            _ => Results.NoContent(),
        };
    }

    private static async Task<IResult> RemoveFavoriteAsync(Guid locationId, ClaimsPrincipal principal, IFavorites favorites, CancellationToken cancellationToken)
    {
        var actor = principal.GetActor();
        await favorites.RemoveAsync(actor.UserId!.Value, locationId, actor, cancellationToken);
        return Results.NoContent();
    }
}
