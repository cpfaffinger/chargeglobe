using ChargeGlobe.Application.Identity;
using ChargeGlobe.Application.Locations;

namespace ChargeGlobe.Application.Favorites;

/// <summary>A favorite location with its current status.</summary>
/// <param name="Removed">No source lists the location any more (lifecycle REMOVED); it has no active EVSEs.</param>
public sealed record Favorite(LocationSummary Location, DateTimeOffset AddedAt, bool Removed);

/// <param name="Attribution">Sources of the locations with the attribution their terms require.</param>
public sealed record FavoriteList(IReadOnlyList<Favorite> Favorites, IReadOnlyList<SourceAttribution> Attribution);

public enum FavoriteOutcome
{
    /// <summary>The location is a favorite (now or already before).</summary>
    Saved,

    /// <summary>There is no active location with this id.</summary>
    UnknownLocation,

    /// <summary>The account has the maximum number of favorites.</summary>
    LimitReached,
}

/// <summary>Locations a signed-in user follows (docs/06, "Favoriten").</summary>
public interface IFavorites
{
    /// <summary>Most favorites one account can have.</summary>
    const int Max = 200;

    /// <summary>The user's favorite locations with their current status, newest first; removed ones are marked.</summary>
    Task<FavoriteList> ListAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Adds the location; idempotent.</summary>
    Task<FavoriteOutcome> AddAsync(Guid userId, Guid locationId, Actor actor, CancellationToken cancellationToken);

    /// <summary>Removes the location; idempotent.</summary>
    Task RemoveAsync(Guid userId, Guid locationId, Actor actor, CancellationToken cancellationToken);
}
