using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChargeGlobe.IntegrationTests;

/// <summary>Region polygons from the OpenStreetMap seed and the region of locations. Own database.</summary>
public class RegionSeedTests(IntegrationFactory factory) : IClassFixture<IntegrationFactory>
{
    public static TheoryData<string, double, double, string?> Places => new()
    {
        { "Wien, Stephansplatz", 48.2085, 16.3731, "AT-9" },
        { "St. Pölten", 48.2047, 15.6256, "AT-3" },
        { "Linz", 48.3069, 14.2858, "AT-4" },
        { "Eisenstadt", 47.8455, 16.5233, "AT-1" },
        { "Graz", 47.0707, 15.4395, "AT-6" },
        { "Klagenfurt", 46.6247, 14.3053, "AT-2" },
        { "Salzburg", 47.8095, 13.0550, "AT-5" },
        { "Innsbruck", 47.2692, 11.4041, "AT-7" },
        { "Bregenz", 47.5031, 9.7471, "AT-8" },
        { "München", 48.1372, 11.5756, "DE-BY" },
        { "Berlin, Alexanderplatz", 52.5219, 13.4132, "DE-BE" },
        { "Bremerhaven", 53.5396, 8.5809, "DE-HB" },
        { "Zürich", 47.3769, 8.5417, null },
    };

    private Task<int> SeedAsync() =>
        factory.WithScopeAsync(services => services.GetRequiredService<RegionSeeder>().SeedAsync(CancellationToken.None));

    private async Task<Location> AddLocationAsync(double latitude, double longitude)
    {
        var location = Location.Create(new GeoPoint(latitude, longitude), "AT", DateTimeOffset.UtcNow);
        await factory.WithDbAsync(db =>
        {
            db.Locations.Add(location);
            return db.SaveChangesAsync();
        });
        return location;
    }

    [Fact]
    public async Task The_API_start_seeds_the_Austrian_and_German_states()
    {
        using var client = factory.Client();
        await client.GetAsync("/api/v1/health");

        var regions = await factory.WithDbAsync(db => db.Regions.OrderBy(region => region.Code).Select(region => region.Code + " " + region.Name).ToListAsync());

        Assert.Equal(
            [
                "AT-1 Burgenland", "AT-2 Kärnten", "AT-3 Niederösterreich", "AT-4 Oberösterreich", "AT-5 Salzburg", "AT-6 Steiermark", "AT-7 Tirol", "AT-8 Vorarlberg", "AT-9 Wien",
                "DE-BB Brandenburg", "DE-BE Berlin", "DE-BW Baden-Württemberg", "DE-BY Bayern", "DE-HB Bremen", "DE-HE Hessen", "DE-HH Hamburg", "DE-MV Mecklenburg-Vorpommern",
                "DE-NI Niedersachsen", "DE-NW Nordrhein-Westfalen", "DE-RP Rheinland-Pfalz", "DE-SH Schleswig-Holstein", "DE-SL Saarland", "DE-SN Sachsen", "DE-ST Sachsen-Anhalt", "DE-TH Thüringen",
            ],
            regions);
        Assert.Equal(0, await SeedAsync());
    }

    [Theory]
    [MemberData(nameof(Places))]
    public async Task A_location_gets_the_region_whose_polygon_contains_it(string place, double latitude, double longitude, string? region)
    {
        var location = await AddLocationAsync(latitude, longitude);

        Assert.True(region == location.RegionCode, $"{place}: expected {region}, got {location.RegionCode}");
    }

    [Fact]
    public async Task A_changed_seed_is_written_again_and_recomputes_the_regions_of_existing_locations()
    {
        var vienna = await AddLocationAsync(48.2085, 16.3731);
        await factory.WithDbAsync(async db =>
        {
            // Deleting the region empties region_code (ON DELETE SET NULL); a renamed region counts as changed.
            await db.Database.ExecuteSqlRawAsync("DELETE FROM region WHERE code = 'AT-9'");
            return await db.Database.ExecuteSqlRawAsync("UPDATE region SET name = 'Renamed' WHERE code = 'AT-3'");
        });
        Assert.Null(await factory.WithDbAsync(db => db.Locations.Where(location => location.Id == vienna.Id).Select(location => location.RegionCode).SingleAsync()));

        Assert.Equal(2, await SeedAsync());

        Assert.Equal("AT-9", await factory.WithDbAsync(db => db.Locations.Where(location => location.Id == vienna.Id).Select(location => location.RegionCode).SingleAsync()));
        Assert.Equal("Niederösterreich", await factory.WithDbAsync(db => db.Regions.Where(region => region.Code == "AT-3").Select(region => region.Name).SingleAsync()));
        Assert.Equal(0, await SeedAsync());
    }

    [Fact]
    public async Task The_crawl_boxes_cover_the_region_polygons()
    {
        var envelopes = await factory.WithDbAsync(db => db.Database
            .SqlQuery<Envelope>($"""
                SELECT code AS "Code", ST_XMin(geom::geometry) AS "MinLongitude", ST_YMin(geom::geometry) AS "MinLatitude",
                       ST_XMax(geom::geometry) AS "MaxLongitude", ST_YMax(geom::geometry) AS "MaxLatitude"
                FROM region
                """)
            .ToListAsync());

        Assert.Equal(RegionBounds.All.Keys.Order(), envelopes.Select(envelope => envelope.Code).Order());
        foreach (var envelope in envelopes)
        {
            var box = RegionBounds.All[envelope.Code];
            Assert.True(
                box.MinLongitude <= envelope.MinLongitude && box.MinLatitude <= envelope.MinLatitude
                && box.MaxLongitude >= envelope.MaxLongitude && box.MaxLatitude >= envelope.MaxLatitude,
                $"{envelope.Code}: box {box} does not cover {envelope.MinLongitude},{envelope.MinLatitude},{envelope.MaxLongitude},{envelope.MaxLatitude}");
        }
    }

    private sealed class Envelope
    {
        public string Code { get; set; } = null!;

        public double MinLongitude { get; set; }

        public double MinLatitude { get; set; }

        public double MaxLongitude { get; set; }

        public double MaxLatitude { get; set; }
    }
}
