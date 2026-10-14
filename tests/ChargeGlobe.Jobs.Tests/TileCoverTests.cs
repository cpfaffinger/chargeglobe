using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Domain;

namespace ChargeGlobe.Jobs.Tests;

public class TileCoverTests
{
    private static TileSite Site(int number, double latitude, double longitude) =>
        new(Guid.Parse($"00000000-0000-0000-0000-{number:D12}"), latitude, longitude);

    [Fact]
    public void Every_location_is_covered_and_equal_input_gives_equal_points()
    {
        // Two clusters of 30 locations, 50 km apart.
        var sites = Enumerable.Range(0, 30).Select(index => Site(index, 48.20 + (index * 0.001), 16.37))
            .Concat(Enumerable.Range(30, 30).Select(index => Site(index, 48.20, 17.05 + (index * 0.001))))
            .ToList();

        var tiles = TileCover.Compute(sites, 30);
        var again = TileCover.Compute([.. Enumerable.Reverse(sites)], 30);

        Assert.Equal(2, tiles.Count);
        Assert.Equal(sites.Select(site => site.LocationId).Order(), tiles.SelectMany(tile => tile.Sites).Select(site => site.LocationId).Order());
        Assert.All(tiles, tile => Assert.Equal(30, tile.Sites.Count));
        Assert.Equal(tiles.Select(tile => tile.Center), again.Select(tile => tile.Center));
        Assert.Empty(TileCover.Compute([], 90));
        Assert.Throws<ArgumentOutOfRangeException>(() => TileCover.Compute(sites, 0));
    }

    [Fact]
    public void A_centre_that_would_lose_its_seed_falls_back_to_the_seed()
    {
        // The three nearest of A are A, B, C; the nearest three of their centre are B, C, D.
        var a = Site(1, 48, 16.0);
        var b = Site(2, 48, 16.2);
        var c = Site(3, 48, 16.21);
        var d = Site(4, 48, 16.22);

        var first = TileCover.Compute([a, b, c, d], 3)[0];

        Assert.Equal(new GeoPoint(48, 16.0), first.Center);
        Assert.Equal([a, b, c], first.Sites);
    }

    [Theory]
    [InlineData(24, false, TilePriority.HOT)]
    [InlineData(6, true, TilePriority.HOT)]
    [InlineData(5, true, TilePriority.NORMAL)]
    [InlineData(23, false, TilePriority.NORMAL)]
    [InlineData(0, true, TilePriority.NORMAL)]
    [InlineData(0, false, TilePriority.QUIET)]
    public void Points_are_classified_by_activity_and_fast_chargers(int changes, bool fastCharger, TilePriority priority)
    {
        Assert.Equal(priority, TileSchedule.Classify(changes, fastCharger));
    }

    [Fact]
    public void Intervals_are_three_times_longer_at_night_in_Vienna()
    {
        // 20:30 UTC is 22:30 in Vienna (summer time), 03:59 UTC is 05:59, 04:00 UTC is 06:00.
        Assert.Equal(TileSchedule.NightFactor, TileSchedule.Factor(new DateTimeOffset(2026, 10, 14, 20, 30, 0, TimeSpan.Zero)));
        Assert.Equal(TileSchedule.NightFactor, TileSchedule.Factor(new DateTimeOffset(2026, 10, 15, 3, 59, 0, TimeSpan.Zero)));
        Assert.Equal(1, TileSchedule.Factor(new DateTimeOffset(2026, 10, 15, 4, 0, 0, TimeSpan.Zero)));
        Assert.Equal(1, TileSchedule.Factor(new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void The_cooldown_grows_linearly_up_to_its_limit_and_a_longer_Retry_After_wins()
    {
        var policy = new CooldownPolicy(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(12));

        Assert.Equal(
            [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(12)],
            new[] { 1, 2, 3 }.Select(failures => policy.After(failures, null)));
        Assert.Equal(TimeSpan.FromMinutes(30), policy.After(1, TimeSpan.FromMinutes(30)));
        Assert.Equal(TimeSpan.FromMinutes(5), policy.After(1, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Each_priority_has_its_interval()
    {
        var intervals = new TileIntervals(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60));

        Assert.Equal(
            [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60)],
            Enum.GetValues<TilePriority>().Select(intervals.Of));
    }
}
