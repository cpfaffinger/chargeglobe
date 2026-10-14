using System.Net;
using System.Text.Json;
using ChargeGlobe.Application.Favorites;
using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Application.Sources;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Infrastructure.Persistence;
using ChargeGlobe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChargeGlobe.IntegrationTests;

/// <summary>/me/favorites on the locations of the E-Control fixture. Own database.</summary>
public class FavoritesTests(IntegrationFactory factory) : IClassFixture<IntegrationFactory>, IAsyncLifetime
{
    private string _accessToken = null!;

    public async Task InitializeAsync()
    {
        _accessToken = (await factory.LoginAsync(IntegrationFactory.AdminEmail, IntegrationFactory.AdminPassword)).AccessToken;
        if (await factory.WithDbAsync(db => db.Locations.AnyAsync()))
        {
            return;
        }

        var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "econtrol", "search-wien.expected.json")));
        var now = DateTimeOffset.UtcNow;
        var locations = JsonSerializer.Deserialize<List<ProviderLocationSnapshot>>(expected.RootElement.GetProperty("locations").GetRawText(), IngestBatch.Json)!;
        await factory.WithScopeAsync(async services =>
        {
            await services.GetRequiredService<ISourceRegistry>().SyncAsync(
                [new SourceDescription("econtrol", "E-Control", SourceTier.OFFICIAL, "Terms", "Datenquelle: Ladestellenverzeichnis der E-Control (ladestellen.at)", [])],
                CancellationToken.None);
            var queue = services.GetRequiredService<IIngestQueue>();
            await queue.EnqueueAsync(
                "econtrol", Guid.CreateVersion7(), new IngestBatch([.. locations.Select(l => new IngestLocation(l with { ObservedAt = now }, null))], []), CancellationToken.None);
            while (await queue.ProcessNextAsync(CancellationToken.None))
            {
            }

            return true;
        });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private HttpClient Client() => factory.Client(_accessToken);

    private Task<Guid> LocationAsync(string name) =>
        factory.WithDbAsync(db => db.Locations.Where(location => location.Name == name).Select(location => location.Id).SingleAsync());

    private Task<Guid> AdminIdAsync() =>
        factory.WithDbAsync(db => db.Users.Where(user => user.Email == IntegrationFactory.AdminEmail).Select(user => user.Id).SingleAsync());

    private async Task<JsonElement> ListAsync()
    {
        var response = await Client().GetAsync("/api/v1/me/favorites");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    [Fact]
    public async Task Favorites_need_an_account()
    {
        var response = await factory.Client().GetAsync("/api/v1/me/favorites");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Favorites_are_added_listed_with_their_status_and_removed_idempotently()
    {
        var stephansplatz = await LocationAsync("Teststation Stephansplatz");
        var ring = await LocationAsync("Testladepark Ring");
        await Client().DeleteAsync($"/api/v1/me/favorites/{stephansplatz}");
        await Client().DeleteAsync($"/api/v1/me/favorites/{ring}");

        Assert.Equal(HttpStatusCode.NoContent, (await Client().PutAsync($"/api/v1/me/favorites/{stephansplatz}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Client().PutAsync($"/api/v1/me/favorites/{stephansplatz}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Client().PutAsync($"/api/v1/me/favorites/{ring}", null)).StatusCode);

        var body = await ListAsync();
        var data = body.GetProperty("data").EnumerateArray().ToList();
        // Newest first.
        Assert.Equal(["Testladepark Ring", "Teststation Stephansplatz"], data.Select(item => item.GetProperty("location").GetProperty("name").GetString()));
        var location = data[1].GetProperty("location");
        Assert.Equal((2, 1, "AVAILABLE"), (location.GetProperty("evsesTotal").GetInt32(), location.GetProperty("evsesAvailable").GetInt32(), location.GetProperty("availability").GetString()));
        Assert.True(data[1].GetProperty("addedAt").GetDateTimeOffset() <= data[0].GetProperty("addedAt").GetDateTimeOffset());
        var meta = body.GetProperty("meta");
        Assert.Equal((2, IFavorites.Max), (meta.GetProperty("returned").GetInt32(), meta.GetProperty("max").GetInt32()));
        Assert.Equal("econtrol", meta.GetProperty("attribution")[0].GetProperty("source").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await Client().DeleteAsync($"/api/v1/me/favorites/{stephansplatz}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Client().DeleteAsync($"/api/v1/me/favorites/{stephansplatz}")).StatusCode);
        Assert.Equal(["Testladepark Ring"], (await ListAsync()).GetProperty("data").EnumerateArray().Select(item => item.GetProperty("location").GetProperty("name").GetString()));

        var audit = await factory.WithDbAsync(db => db.AuditLog
            .Where(entry => entry.Target == stephansplatz.ToString())
            .Select(entry => entry.Action)
            .ToListAsync());
        Assert.Equal(1, audit.Count(action => action == "favorite.add"));
        Assert.Equal(1, audit.Count(action => action == "favorite.remove"));
    }

    [Fact]
    public async Task Unknown_locations_are_rejected_and_inactive_ones_left_out()
    {
        var problem = await (await Client().PutAsync($"/api/v1/me/favorites/{Guid.NewGuid()}", null)).AssertProblemAsync(HttpStatusCode.NotFound);
        Assert.Contains("listLocations", problem.GetProperty("detail").GetString());

        var removed = Location.Create(new GeoPoint(47.8, 13.04), "AT", DateTimeOffset.UtcNow);
        removed.Lifecycle = Lifecycle.REMOVED;
        var userId = await AdminIdAsync();
        await factory.WithDbAsync(db =>
        {
            db.Locations.Add(removed);
            db.FavoriteLocations.Add(new FavoriteLocation { UserId = userId, LocationId = removed.Id, CreatedAt = DateTimeOffset.UtcNow });
            return db.SaveChangesAsync();
        });

        Assert.DoesNotContain(
            removed.Id.ToString(),
            (await ListAsync()).GetProperty("data").EnumerateArray().Select(item => item.GetProperty("location").GetProperty("id").GetString()));
        // A favorite that is no longer active stays saved.
        Assert.Equal(HttpStatusCode.NoContent, (await Client().PutAsync($"/api/v1/me/favorites/{removed.Id}", null)).StatusCode);
    }

    [Fact]
    public async Task An_account_has_at_most_200_favorites()
    {
        var userId = await AdminIdAsync();
        var existing = await factory.WithDbAsync(db => db.FavoriteLocations.CountAsync(favorite => favorite.UserId == userId));
        var filler = Enumerable.Range(0, IFavorites.Max - existing)
            .Select(index => Location.Create(new GeoPoint(46.5 + (index * 0.001), 14.0), "AT", DateTimeOffset.UtcNow))
            .ToList();
        await factory.WithDbAsync(db =>
        {
            db.Locations.AddRange(filler);
            db.FavoriteLocations.AddRange(filler.Select(location => new FavoriteLocation { UserId = userId, LocationId = location.Id, CreatedAt = DateTimeOffset.UtcNow }));
            return db.SaveChangesAsync();
        });
        var another = Location.Create(new GeoPoint(46.4, 14.0), "AT", DateTimeOffset.UtcNow);
        await factory.WithDbAsync(db =>
        {
            db.Locations.Add(another);
            return db.SaveChangesAsync();
        });

        var problem = await (await Client().PutAsync($"/api/v1/me/favorites/{another.Id}", null)).AssertProblemAsync(HttpStatusCode.Conflict);

        Assert.Contains("200", problem.GetProperty("detail").GetString());
        await factory.WithDbAsync(db => db.FavoriteLocations.Where(favorite => favorite.UserId == userId).ExecuteDeleteAsync());
    }
}
