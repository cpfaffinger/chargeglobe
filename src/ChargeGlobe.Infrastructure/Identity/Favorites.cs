using ChargeGlobe.Application.Favorites;
using ChargeGlobe.Application.Identity;
using ChargeGlobe.Application.Locations;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Infrastructure.Identity;

/// <summary>Favorites in <c>favorite_location</c>; changes are recorded in the audit log.</summary>
public sealed class Favorites(AppDbContext db, ILocationQueries locations, AuditLog audit, TimeProvider timeProvider) : IFavorites
{
    public async Task<FavoriteList> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        var favorites = await db.FavoriteLocations.AsNoTracking()
            .Where(favorite => favorite.UserId == userId)
            .ToDictionaryAsync(favorite => favorite.LocationId, favorite => favorite.CreatedAt, cancellationToken);
        List<Guid> ids = [.. favorites.Keys];
        var page = await locations.ByIdsAsync(ids, includeRemoved: true, cancellationToken);
        var removed = await db.Locations.AsNoTracking()
            .Where(location => ids.Contains(location.Id) && location.Lifecycle == Lifecycle.REMOVED)
            .Select(location => location.Id)
            .ToListAsync(cancellationToken);
        return new FavoriteList(
            [.. page.Locations
                .Select(location => new Favorite(location, favorites[location.Id], removed.Contains(location.Id)))
                .OrderByDescending(favorite => favorite.AddedAt)],
            page.Attribution);
    }

    public async Task<FavoriteOutcome> AddAsync(Guid userId, Guid locationId, Actor actor, CancellationToken cancellationToken)
    {
        if (await db.FavoriteLocations.AnyAsync(favorite => favorite.UserId == userId && favorite.LocationId == locationId, cancellationToken))
        {
            return FavoriteOutcome.Saved;
        }

        if (!await db.Locations.AnyAsync(location => location.Id == locationId && location.Lifecycle == Lifecycle.ACTIVE, cancellationToken))
        {
            return FavoriteOutcome.UnknownLocation;
        }

        if (await db.FavoriteLocations.CountAsync(favorite => favorite.UserId == userId, cancellationToken) >= IFavorites.Max)
        {
            return FavoriteOutcome.LimitReached;
        }

        db.FavoriteLocations.Add(new FavoriteLocation { UserId = userId, LocationId = locationId, CreatedAt = timeProvider.GetUtcNow() });
        audit.Record("favorite.add", locationId.ToString(), actor);
        await db.SaveChangesAsync(cancellationToken);
        return FavoriteOutcome.Saved;
    }

    public async Task RemoveAsync(Guid userId, Guid locationId, Actor actor, CancellationToken cancellationToken)
    {
        var favorite = await db.FavoriteLocations.FirstOrDefaultAsync(item => item.UserId == userId && item.LocationId == locationId, cancellationToken);
        if (favorite is null)
        {
            return;
        }

        db.FavoriteLocations.Remove(favorite);
        audit.Record("favorite.remove", locationId.ToString(), actor);
        await db.SaveChangesAsync(cancellationToken);
    }
}
