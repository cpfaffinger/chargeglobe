using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Providers.Abstractions;
using ChargeGlobe.Providers.EControl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace ChargeGlobe.Providers.Tests;

public class EControlClientTests
{
    [Fact]
    public async Task Search_sends_key_referer_and_invariant_coordinates_and_drops_personal_fields()
    {
        var handler = new StubHandler(_ => StubHandler.Json(EControl.Fixture("search-wien.json")));

        var result = await EControl.Client(handler, new StubSettings("secret-key", "ogb.net")).SearchAsync(new GeoPoint(48.2085, 16.3731), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.e-control.at/charge/1.0/search?latitude=48.2085&longitude=16.3731", request.RequestUri!.ToString());
        Assert.Equal(["secret-key"], request.Headers.GetValues("apiKey"));
        Assert.Equal(new Uri("https://ogb.net/"), request.Headers.Referrer);
        Assert.Equal(3, result.Stations.Count);
        var (station, raw) = result.Stations[0];
        Assert.Equal(("AT", "TST", "ETEST0001"), (station.CountryId, station.OperatorId, station.StationId));
        Assert.False(raw.TryGetProperty("contactName", out _));
        Assert.False(raw.TryGetProperty("email", out _));
        Assert.False(raw.TryGetProperty("owner", out _));
        Assert.True(raw.TryGetProperty("label", out _));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData("ogb.net", "https://ogb.net/")]
    [InlineData(" ogb.net/ ", "https://ogb.net/")]
    [InlineData("https://www.example.org/app", "https://www.example.org/app")]
    [InlineData("http://example.org/", "http://example.org/")]
    public void Referer_accepts_URLs_and_bare_host_names(string? setting, string? expected)
    {
        Assert.Equal(expected, EControlClient.RefererUri(setting)?.ToString());
    }

    [Fact]
    public async Task Search_without_referer_sends_none()
    {
        var handler = new StubHandler(_ => StubHandler.Json("[]"));

        var result = await EControl.Client(handler, new StubSettings(referer: null)).SearchAsync(new GeoPoint(48, 16), CancellationToken.None);

        Assert.Null(Assert.Single(handler.Requests).Headers.Referrer);
        Assert.Empty(result.Stations);
    }

    [Fact]
    public async Task Search_fails_without_key_on_error_status_and_on_a_null_body()
    {
        var handler = new StubHandler(_ => StubHandler.Json("Zur Nutzung ist eine Registrierung notwendig.", HttpStatusCode.Unauthorized));
        var point = new GeoPoint(48, 16);

        var missing = await Assert.ThrowsAsync<ProviderNotConfiguredException>(
            () => EControl.Client(handler, new StubSettings(apiKey: null)).SearchAsync(point, CancellationToken.None));
        Assert.Contains("provider.api_key", missing.Message);
        Assert.Empty(handler.Requests);

        var unauthorized = await Assert.ThrowsAsync<HttpRequestException>(() => EControl.Client(handler).SearchAsync(point, CancellationToken.None));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        handler.Respond = _ => StubHandler.Json("null");
        await Assert.ThrowsAsync<JsonException>(() => EControl.Client(handler).SearchAsync(point, CancellationToken.None));
    }

    [Fact]
    public async Task Register_page_is_requested_with_paging_and_mapped_to_the_common_form()
    {
        var handler = new StubHandler(_ => StubHandler.Json(EControl.Fixture("stations-register.json")));

        var page = await EControl.Client(handler, new StubSettings("secret-key", "ogb.net")).SearchStationsAsync(2000, 1000, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.e-control.at/charge/1.0/search/stations?fromIndex=2000&maxResults=1000", request.RequestUri!.ToString());
        Assert.Equal(["secret-key"], request.Headers.GetValues("apiKey"));
        Assert.Equal(new Uri("https://ogb.net/"), request.Headers.Referrer);
        Assert.Equal((6, 6), (page.TotalResults, page.Stations.Count));
        var (station, raw) = page.Stations[0];
        Assert.Equal(("AT", "VIE", "E1436905", "ACTIVE"), (station.CountryId, station.OperatorId, station.StationId, station.StationStatus));
        Assert.Equal(new LatLonDto(48.197447999999994, 16.335987), station.Location);
        Assert.Equal((true, false, false, 0), (station.GreenEnergy, station.FreeParking, station.RoofedParking, station.BarrierFreeParkingPlaces));
        Assert.StartsWith("https://www.wienenergie.at/", station.PriceUrl, StringComparison.Ordinal);
        var point = station.Points![0];
        Assert.Equal(("AT*VIE*E1150650004*6", 11d, 56m, (string?)null), (point.EvseId, point.CapacityKw, point.PriceCentKwh, point.Status));
        Assert.Equal(["APP", "RFID_READER", "CREDIT_CARD"], point.AuthenticationMode!);
        Assert.Equal((true, "IEC_62196_T2"), (point.Roaming, point.ConnectorType![0].Key));
        Assert.Equal(["AC_3_PHASE"], point.ElectricityType!);
        Assert.False(raw.TryGetProperty("contactName", out _));
        Assert.False(raw.TryGetProperty("owner", out _));
        Assert.True(raw.TryGetProperty("evseStationId", out _));
    }

    [Fact]
    public async Task Register_page_without_stations_or_total_is_empty_and_errors_name_the_endpoint()
    {
        var handler = new StubHandler(_ => StubHandler.Json("""{"stations":null}"""));

        var page = await EControl.Client(handler).SearchStationsAsync(0, 1000, CancellationToken.None);
        Assert.Equal((0, 0), (page.TotalResults, page.Stations.Count));

        handler.Respond = _ => StubHandler.Json("""{"totalResults":1,"stations":[{"evseStationId":"E1","location":null,"points":null}]}""");
        var bare = Assert.Single((await EControl.Client(handler).SearchStationsAsync(0, 1000, CancellationToken.None)).Stations).Station;
        Assert.Equal("E1", bare.StationId);
        Assert.Null(bare.Location);
        Assert.Null(bare.Points);

        handler.Respond = _ => StubHandler.Json("Registrierung notwendig", HttpStatusCode.Forbidden);
        var forbidden = await Assert.ThrowsAsync<HttpRequestException>(() => EControl.Client(handler).SearchStationsAsync(0, 1000, CancellationToken.None));
        Assert.Equal("E-Control search/stations returned 403.", forbidden.Message);
    }
}

public class EControlMapperTests
{
    private static readonly EControlMapper Mapper = EControlMapper.CreateDefault();

    private static StationDto Station(LatLonDto? location, string? stationId = "E1", IReadOnlyList<PointDto>? points = null) =>
        new("AT", "TST", stationId, "ACTIVE", "Test", "1010", "Wien", "Testgasse 1", location, 0.1, "Test GmbH", null, points);

    [Fact]
    public void Records_without_ID_or_with_invalid_coordinates_are_reported_and_skipped()
    {
        var issues = new List<ProviderMappingIssue>();
        var raw = JsonDocument.Parse("{}").RootElement;

        Assert.Null(Mapper.ToLocation(Station(new LatLonDto(48, 16), stationId: null), raw, EControl.Now, issues));
        Assert.Null(Mapper.ToLocation(Station(new LatLonDto(91, 16)), raw, EControl.Now, issues));
        Assert.Null(Mapper.ToLocation(Station(new LatLonDto(48, null)), raw, EControl.Now, issues));
        Assert.Null(Mapper.ToLocation(Station(new LatLonDto(48, 181)), raw, EControl.Now, issues));
        // 0/0 is the placeholder of records without a position.
        Assert.Null(Mapper.ToLocation(Station(new LatLonDto(0, 0)), raw, EControl.Now, issues));

        Assert.Equal(
            [(null, "Station without country, operator or station ID."), ("AT*TST*E1", "Station without valid coordinates.")],
            issues.Select(issue => (issue.ExternalId, issue.Problem)).Distinct());
        Assert.All(issues, issue => Assert.Equal("econtrol", issue.ProviderKey));
        Assert.Empty(Mapper.ToStatuses(Station(null, stationId: null), EControl.Now));
        Assert.Empty(Mapper.ToPrices(Station(null, stationId: null), raw, EControl.Now));
    }

    [Fact]
    public void Station_without_points_or_opening_hours_has_no_EVSEs_and_unknown_opening_times()
    {
        var location = Mapper.ToLocation(Station(new LatLonDto(48.2, 16.37)), JsonDocument.Parse("{}").RootElement, EControl.Now, []);

        Assert.NotNull(location);
        Assert.Empty(location.Evses);
        Assert.Null(location.OpeningTimes);
        Assert.Equal(new GeoPoint(48.2, 16.37), location.Coordinates);
    }

    [Fact]
    public void Facilities_payment_and_country_come_from_the_station_and_its_points()
    {
        var point = new PointDto(
            "CH*ECU*E1", 11, false, 50, null, null, null, null, [new ConnectorTypeDto("STYPE2", true)], ["AC_3_PHASE"], null,
            ["CREDIT_CARD", "APP", "SMS", "APP", " QR "], false);
        var station = new StationDto(
            "CH", "ECU", "E1", "ACTIVE", "Wallbox", "1010", "Wien", "Gasse 1", new LatLonDto(48.2, 16.37), null, "eCarUp AG", null, [point],
            true, false, true, 2, "www.example.at/tarife.pdf");

        var location = Mapper.ToLocation(station, JsonDocument.Parse("{}").RootElement, EControl.Now, [])!;

        // The address lies in Austria; the EVSE-ID tells the country of the operator.
        Assert.Equal(("AT", "CH", "ECU"), (location.Address!.Country, location.OperatorCountry, location.OperatorId));
        Assert.Equal(new ProviderFacilities(true, false, true, 2, "https://www.example.at/tarife.pdf"), location.Facilities);
        var evse = Assert.Single(location.Evses);
        Assert.Equal([AuthMethod.APP, AuthMethod.QR_CODE, AuthMethod.CREDIT_CARD_PAYABLE], evse.AuthMethods!);
        Assert.False(evse.Roaming);
        // The register has no live status: the EVSE carries none instead of UNKNOWN.
        Assert.Null(evse.Status);
        Assert.Null(evse.StatusRaw);
        Assert.Equal((ConnectorStandard.IEC_62196_T2, ConnectorFormat.SOCKET), (evse.Connectors[0].Standard, evse.Connectors[0].Format));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData("https://www.wienenergie.at/tarife/", "https://www.wienenergie.at/tarife/")]
    [InlineData(" http://example.org ", "http://example.org/")]
    [InlineData("edriver.salzachsonne.at", "https://edriver.salzachsonne.at/")]
    [InlineData("ftp://example.org/tarife", null)]
    [InlineData("siehe App", null)]
    [InlineData("localhost", null)]
    public void Price_page_becomes_an_absolute_web_address_or_nothing(string? priceUrl, string? expected)
    {
        var station = new StationDto("AT", "TST", "E1", "ACTIVE", null, null, null, null, new LatLonDto(48, 16), null, null, null, null, PriceUrl: priceUrl);

        Assert.Equal(expected, Mapper.ToLocation(station, JsonDocument.Parse("{}").RootElement, EControl.Now, [])!.Facilities!.PriceUrl);
    }

    [Fact]
    public void Authentication_modes_without_a_list_stay_unknown()
    {
        Assert.Null(Mapper.AuthMethods(null));
        Assert.Empty(Mapper.AuthMethods([])!);
        Assert.Empty(Mapper.AuthMethods(["SMS"])!);
    }

    [Fact]
    public void Every_connector_type_of_the_source_has_a_mapping()
    {
        using var enumeration = JsonDocument.Parse(EControl.Fixture("enumerations-connectorTypes.json"));
        var keys = enumeration.RootElement.EnumerateArray().Select(item => item.GetProperty("key").GetString()!).ToList();

        // Types without an OCPI 2.2.1 counterpart are mapped to UNKNOWN on purpose (Mapping/connector-mapping.json).
        string[] withoutOcpiType =
        [
            "S309-1P-32A", "OTHER1PHMAX16A", "OTHER1PHOVER16A", "OTHER3PH", "PAN", "WINDUCTIVE", "WRESONANT", "UNDETERMINED", "UNKNOWN", "MCS",
        ];
        Assert.Equal(63, keys.Count);
        Assert.Equal(
            withoutOcpiType.Order(),
            keys.Where(key => Mapper.Connector(key).Standard == ConnectorStandard.UNKNOWN).Order());

        // The source lists one key with a trailing blank; OCPI-like keys are matched without regard to case.
        Assert.Equal(ConnectorStandard.DOMESTIC_B, Mapper.Connector("DOMESTIC_B ").Standard);
        Assert.Equal(ConnectorStandard.IEC_60309_2_three_64, Mapper.Connector("IEC_60309_2_THREE_64").Standard);
        Assert.Equal(new ConnectorMappingEntry(ConnectorStandard.UNKNOWN, null), Mapper.Connector("NEW_TYPE"));
    }

    [Fact]
    public void Every_authentication_mode_of_the_source_has_a_mapping()
    {
        using var enumeration = JsonDocument.Parse(EControl.Fixture("enumerations-authenticationModes.json"));
        var keys = enumeration.RootElement.EnumerateArray().Select(item => item.GetProperty("key").GetString()!).ToList();

        var methods = Mapper.AuthMethods(keys)!;

        Assert.Equal(11, keys.Count);
        Assert.Equal(Enum.GetValues<AuthMethod>(), methods);
    }

    [Fact]
    public void Point_without_status_maps_to_UNKNOWN_with_an_empty_raw_value()
    {
        var point = new PointDto("AT*TST*E1", null, null, null, null, null, null, null, null, null, null);

        var status = Assert.Single(Mapper.ToStatuses(Station(new LatLonDto(48, 16), points: [point]), EControl.Now));

        Assert.Equal((EvseStatus.UNKNOWN, string.Empty), (status.Status, status.StatusRaw));
    }

    [Fact]
    public void Default_tables_contain_the_status_and_connector_vocabulary_of_the_source()
    {
        Assert.Equal(EvseStatus.CHARGING, Mapper.StatusMapping.Map("OCCUPIED"));
        Assert.Equal(EvseStatus.OUTOFORDER, Mapper.StatusMapping.Map("outoforder"));
        Assert.Equal(10, Mapper.StatusMapping.Entries.Count);
    }
}

public class SearchGridTests
{
    private static readonly BoundingBox Box = new(16.0, 48.0, 16.4, 48.4);

    /// <summary>A full page of stations around the point, all within <paramref name="reachKm"/>.</summary>
    private static SearchResult Page(GeoPoint center, int count, double reachKm, string prefix) =>
        new([.. Enumerable.Range(0, count).Select(index =>
        {
            var json = EControl.Station($"{prefix}{index}", center.Latitude, center.Longitude, reachKm);
            var raw = JsonDocument.Parse(json).RootElement;
            return (raw.Deserialize<StationDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!, raw);
        })]);

    [Fact]
    public async Task A_full_page_closer_than_the_corners_splits_the_cell_into_four()
    {
        var centers = new List<GeoPoint>();
        var grid = new SearchGrid(
            (center, _) =>
            {
                centers.Add(center);
                // The first (whole box) search is a dense full page; the quarters are sparse.
                return Task.FromResult(centers.Count == 1 ? Page(center, SearchGrid.PageSize, 1, "dense") : Page(center, 3, 5, $"q{centers.Count}-"));
            },
            100);

        var stations = await grid.CoverAsync(Box, CancellationToken.None).ToListAsync();

        Assert.Equal(5, grid.Requests);
        Assert.False(grid.Exhausted);
        Assert.Equal(new GeoPoint(48.2, 16.2), centers[0]);
        Assert.Equal(
            [(48.1, 16.1), (48.1, 16.3), (48.3, 16.1), (48.3, 16.3)],
            centers.Skip(1).Select(center => (Math.Round(center.Latitude, 6), Math.Round(center.Longitude, 6))));
        Assert.Equal(SearchGrid.PageSize + (4 * 3), stations.Count);
    }

    [Fact]
    public async Task Each_station_is_yielded_once_and_stations_outside_the_box_are_dropped()
    {
        var inside = EControl.Station("A", 48.1, 16.1, 1);
        var outside = EControl.Station("B", 49.5, 16.1, 1);
        var withoutId = """{"countryId":"AT","location":{"lat":48.1,"lon":16.1},"distance":1}""";
        var withoutLocation = """{"countryId":"AT","operatorId":"TST","stationId":"C","distance":1}""";
        var handler = new StubHandler(_ => StubHandler.Json($"[{inside},{inside},{outside},{withoutId},{withoutLocation}]"));
        var grid = new SearchGrid(EControl.Client(handler).SearchAsync, 10);

        var stations = await grid.CoverAsync(Box, CancellationToken.None).ToListAsync();

        Assert.Equal(["A", null, "C"], stations.Select(item => item.Station.StationId));
        Assert.Equal(1, grid.Requests);
    }

    [Fact]
    public async Task The_budget_ends_a_run_and_tiny_cells_are_not_split_further()
    {
        var exhausted = new SearchGrid((center, _) => Task.FromResult(Page(center, SearchGrid.PageSize, 0.001, $"{center}")), 3);
        await exhausted.CoverAsync(Box, CancellationToken.None).ToListAsync();
        Assert.True(exhausted.Exhausted);
        Assert.Equal(3, exhausted.Requests);

        var tiny = new BoundingBox(16.3700, 48.2080, 16.3701, 48.2081);
        var stacked = new SearchGrid((center, _) => Task.FromResult(Page(center, SearchGrid.PageSize, 0, "same")), 10);
        var stations = await stacked.CoverAsync(tiny, CancellationToken.None).ToListAsync();
        Assert.Equal(1, stacked.Requests);
        Assert.False(stacked.Exhausted);
        Assert.Equal(SearchGrid.PageSize, stations.Count);
    }

    [Fact]
    public void Distance_is_the_great_circle_distance()
    {
        // Vienna Stephansplatz to St. Pölten Rathausplatz: about 56 km.
        var distance = SearchGrid.DistanceKm(new GeoPoint(48.2085, 16.3731), new GeoPoint(48.2047, 15.6256));

        Assert.InRange(distance, 55, 57);
        Assert.Equal(0, SearchGrid.DistanceKm(new GeoPoint(48, 16), new GeoPoint(48, 16)));
    }
}

public class RateLimitingHandlerTests
{
    private static HttpClient Client(RateLimiter limiter, FakeTimeProvider time, StubHandler inner) =>
        new(new RateLimitingHandler(limiter, time) { InnerHandler = inner }) { BaseAddress = new Uri("https://source.test/") };

    private static RateLimiter Unlimited() => RateLimitingHandler.CreateLimiter(new RateLimitPolicy(60_000, 100));

    [Fact]
    public void The_bucket_follows_the_policy()
    {
        using var limiter = RateLimitingHandler.CreateLimiter(new RateLimitPolicy(PerMinute: 40, Burst: 2));

        Assert.True(limiter.AttemptAcquire().IsAcquired);
        Assert.True(limiter.AttemptAcquire().IsAcquired);
        Assert.False(limiter.AttemptAcquire().IsAcquired);
    }

    [Fact]
    public async Task A_request_without_a_token_fails()
    {
        using var limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 1, TokensPerPeriod = 1, ReplenishmentPeriod = TimeSpan.FromHours(1), QueueLimit = 0, AutoReplenishment = false,
        });
        var inner = new StubHandler(_ => StubHandler.Json("[]"));
        using var client = Client(limiter, new FakeTimeProvider(), inner);

        (await client.GetAsync("a")).EnsureSuccessStatusCode();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("b"));

        Assert.Contains("budget", exception.Message);
        Assert.Single(inner.Requests);
    }

    [Fact]
    public async Task Too_many_requests_with_Retry_After_is_repeated_once_after_the_delay()
    {
        var time = new FakeTimeProvider(EControl.Now);
        var calls = 0;
        var inner = new StubHandler(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.FromSeconds(30)) } }
            : StubHandler.Json("[]"));
        using var client = Client(Unlimited(), time, inner);

        var response = await CompleteOnFakeClockAsync(client.GetAsync("a"), time);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Requests.Count);
        Assert.InRange(time.GetUtcNow() - EControl.Now, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(35));
    }

    [Fact]
    public async Task Retry_After_as_date_is_clamped_and_responses_without_it_are_returned()
    {
        var time = new FakeTimeProvider(EControl.Now);
        var calls = 0;
        var inner = new StubHandler(_ => ++calls switch
        {
            1 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Headers = { RetryAfter = new(EControl.Now.AddHours(1)) } },
            2 => StubHandler.Json("[]"),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
        });
        using var client = Client(Unlimited(), time, inner);

        Assert.Equal(HttpStatusCode.OK, (await CompleteOnFakeClockAsync(client.GetAsync("a"), time)).StatusCode);
        Assert.InRange(time.GetUtcNow() - EControl.Now, RateLimitingHandler.MaxRetryAfter, RateLimitingHandler.MaxRetryAfter + TimeSpan.FromSeconds(5));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("b")).StatusCode);
        Assert.Equal(3, inner.Requests.Count);
    }

    /// <summary>Moves the fake clock forward in one-second steps until the request has completed.</summary>
    private static async Task<HttpResponseMessage> CompleteOnFakeClockAsync(Task<HttpResponseMessage> pending, FakeTimeProvider time)
    {
        for (var step = 0; !pending.IsCompleted && step < 1000; step++)
        {
            await Task.Delay(1);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        return await pending;
    }
}

public class EControlProviderTests
{
    [Fact]
    public void Descriptor_describes_the_official_register()
    {
        var descriptor = EControlProvider.DescriptorValue;

        Assert.Equal(("econtrol", SourceTier.OFFICIAL, "AT"), (descriptor.Key, descriptor.Tier, descriptor.Countries.Single()));
        Assert.True(descriptor.Capabilities.HasFlag(ProviderCapabilities.EvseIds | ProviderCapabilities.LiveStatusPerEvse | ProviderCapabilities.AdHocPrice));
        // The register is read page by page in one go; the live status still needs the search grid.
        Assert.True(descriptor.Capabilities.HasFlag(ProviderCapabilities.BulkAll | ProviderCapabilities.BulkByBoundingBox));
        // 2,500 requests per hour allowed by the terms of use.
        Assert.True(descriptor.RateLimit.PerMinute * 60 <= 2500);
        Assert.Contains("ladestellen.at", descriptor.AttributionText);
        Assert.Same(descriptor, EControl.Provider(new StubHandler(_ => StubHandler.Json("[]"))).Provider.Descriptor);
    }

    [Fact]
    public async Task Inventory_for_another_country_makes_no_request()
    {
        var handler = new StubHandler(_ => StubHandler.Json("[]"));

        var locations = await EControl.Provider(handler).Provider.FetchInventoryAsync(new InventoryQuery(null, "DE", null), CancellationToken.None).ToListAsync();

        Assert.Empty(locations);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Without_bounding_box_the_search_covers_Austria()
    {
        var handler = new StubHandler(_ => StubHandler.Json("[]"));

        await EControl.Provider(handler).Provider.FetchStatusAsync(new StatusQuery(null, null), CancellationToken.None).ToListAsync();

        var center = new GeoPoint((EControlProvider.Austria.MinLatitude + EControlProvider.Austria.MaxLatitude) / 2, (EControlProvider.Austria.MinLongitude + EControlProvider.Austria.MaxLongitude) / 2);
        Assert.Contains($"latitude={center.Latitude.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)}", Assert.Single(handler.Requests).RequestUri!.Query);
    }

    [Fact]
    public async Task A_run_that_exceeds_the_budget_is_logged()
    {
        // Every answer is a full page closer than any corner, so the grid keeps splitting.
        var page = "[" + string.Join(",", Enumerable.Range(0, SearchGrid.PageSize).Select(index => EControl.Station($"S{index}", 47.5, 13.3, 0.0001))) + "]";
        var handler = new StubHandler(_ => StubHandler.Json(page));
        var (provider, logger) = EControl.Provider(handler);

        await provider.FetchStatusAsync(new StatusQuery(null, null), CancellationToken.None).ToListAsync();

        Assert.Equal(EControlProvider.MaxRequestsPerRun, handler.Requests.Count);
        Assert.Contains($"E-Control fetch stopped after {EControlProvider.MaxRequestsPerRun} requests before the area was covered", logger.Messages);
    }

    [Fact]
    public async Task Inventory_reads_the_register_page_by_page_until_the_total()
    {
        var handler = new StubHandler(request => StubHandler.Json(request.RequestUri!.Query.Contains("fromIndex=0", StringComparison.Ordinal)
            ? RegisterPage(3, 48.2, 16.37, "A", "B")
            : RegisterPage(3, 48.2, 16.37, "C")));

        var locations = await EControl.Provider(handler).Provider.FetchInventoryAsync(new InventoryQuery(null, null, null), CancellationToken.None).ToListAsync();

        Assert.Equal(["AT*TST*A", "AT*TST*B", "AT*TST*C"], locations.Select(location => location.ExternalId));
        Assert.Equal(
            ["?fromIndex=0&maxResults=1000", "?fromIndex=2&maxResults=1000"],
            handler.Requests.Select(request => request.RequestUri!.Query));
    }

    [Fact]
    public async Task Inventory_keeps_the_stations_in_the_box_and_stops_at_an_empty_page()
    {
        var calls = 0;
        var handler = new StubHandler(_ => StubHandler.Json(++calls switch
        {
            1 => RegisterPage(10, 48.2, 16.37, "VIENNA"),
            2 => RegisterPage(10, 47.07, 15.44, "GRAZ"),
            _ => RegisterPage(10, 0, 0),
        }));
        var vienna = new BoundingBox(16.18, 48.11, 16.58, 48.33);

        var locations = await EControl.Provider(handler).Provider.FetchInventoryAsync(new InventoryQuery(vienna, "AT", null), CancellationToken.None).ToListAsync();

        Assert.Equal("AT*TST*VIENNA", Assert.Single(locations).ExternalId);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task A_register_that_never_ends_is_cut_at_the_budget()
    {
        var handler = new StubHandler(_ => StubHandler.Json(RegisterPage(int.MaxValue, 48.2, 16.37, "SAME")));
        var (provider, logger) = EControl.Provider(handler);

        await provider.FetchPricesAsync(new PriceQuery(null, null), CancellationToken.None).ToListAsync();

        Assert.Equal(EControlProvider.MaxRequestsPerRun, handler.Requests.Count);
        Assert.Contains($"E-Control fetch stopped after {EControlProvider.MaxRequestsPerRun} requests before the register was read", logger.Messages);
    }

    /// <summary>A register page with one active station per ID at the given position.</summary>
    private static string RegisterPage(int total, double lat, double lon, params string[] ids)
    {
        var at = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"\"latitude\":{lat},\"longitude\":{lon}");
        var stations = ids.Select(id =>
            $"{{\"evseCountryId\":\"AT\",\"evseOperatorId\":\"TST\",\"evseStationId\":\"{id}\",\"status\":\"ACTIVE\",\"location\":{{{at}}},\"points\":[]}}");
        return $"{{\"totalResults\":{total},\"stations\":[{string.Join(",", stations)}]}}";
    }

    [Theory]
    [InlineData("search-wien.json", HttpStatusCode.OK, true, 200, null)]
    [InlineData("[]", HttpStatusCode.OK, false, 200, "The search returned no stations.")]
    [InlineData("Registrierung notwendig", HttpStatusCode.Unauthorized, false, 401, "E-Control search returned 401.")]
    [InlineData("{", HttpStatusCode.OK, false, 200, "Unexpected format")]
    public async Task Probe_reports_the_state_of_the_source(string body, HttpStatusCode status, bool healthy, int httpStatus, string? message)
    {
        var content = body.EndsWith(".json", StringComparison.Ordinal) ? EControl.Fixture(body) : body;
        var (provider, _) = EControl.Provider(new StubHandler(_ => StubHandler.Json(content, status)));

        var result = await provider.ProbeAsync(CancellationToken.None);

        Assert.Equal((healthy, (int?)httpStatus), (result.Healthy, result.HttpStatus));
        if (message is null)
        {
            Assert.Null(result.Message);
        }
        else
        {
            Assert.StartsWith(message, result.Message);
        }
    }

    [Fact]
    public async Task Probe_without_key_reports_the_missing_setting()
    {
        var (provider, _) = EControl.Provider(new StubHandler(_ => StubHandler.Json("[]")), new StubSettings(apiKey: null));

        var result = await provider.ProbeAsync(CancellationToken.None);

        Assert.False(result.Healthy);
        Assert.Null(result.HttpStatus);
        Assert.Contains("provider.api_key", result.Message);
    }

    [Fact]
    public void Registration_resolves_the_adapter_for_all_source_interfaces()
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IProviderSettings>(new StubSettings())
            .AddEControlProvider()
            .BuildServiceProvider();

        Assert.IsType<EControlProvider>(services.GetRequiredService<IProvider>());
        Assert.IsType<EControlProvider>(services.GetRequiredService<IInventorySource>());
        Assert.IsType<EControlProvider>(services.GetRequiredService<IStatusSource>());
        Assert.IsType<EControlProvider>(services.GetRequiredService<IPriceSource>());
        Assert.IsType<EControlProvider>(services.GetRequiredService<IProviderHealth>());
        var http = services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(EControlClient));
        Assert.Equal(EControlClient.BaseAddress, http.BaseAddress);
        Assert.Contains("chargeglobe", http.DefaultRequestHeaders.UserAgent.ToString());
    }
}
