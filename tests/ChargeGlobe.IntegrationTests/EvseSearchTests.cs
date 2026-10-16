using System.Net;
using System.Text.Json;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.IntegrationTests;

/// <summary>GET /api/v1/evses: filters, order and cursor paging. A database of its own with known EVSEs.</summary>
public class EvseSearchTests(IntegrationFactory factory) : IClassFixture<IntegrationFactory>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private Guid _station;
    private string? _token;

    public async Task InitializeAsync()
    {
        if (!await factory.WithDbAsync(db => db.Evses.AnyAsync()))
        {
            await SeedAsync();
        }

        _station = await factory.WithDbAsync(db => db.ChargingStations.Select(station => station.Id).SingleAsync());
    }

    private Task SeedAsync() =>
        factory.WithDbAsync(async db =>
        {
            var evn = Operator.Create("EVN", "AT", "EVN");
            var smatrics = Operator.Create("SMATRICS", "AT", "SMA");
            db.Operators.AddRange(evn, smatrics);
            var stPoelten = Location.Create(new GeoPoint(48.204, 15.623), "AT", Now.AddDays(-5));
            stPoelten.Name = "Rathausplatz";
            stPoelten.City = "St. Pölten";
            var vienna = Location.Create(new GeoPoint(48.2085, 16.3731), "AT", Now.AddDays(-5));
            vienna.Name = "Stephansplatz";
            vienna.City = "Wien";
            var gone = Location.Create(new GeoPoint(48.21, 16.38), "AT", Now.AddDays(-5));
            gone.Lifecycle = Lifecycle.REMOVED;
            db.Locations.AddRange(stPoelten, vienna, gone);
            var station = ChargingStation.Create(stPoelten.Id);
            station.Label = "AT*EVN*E2453";
            db.ChargingStations.Add(station);

            Evse Add(string evseId, Location location, Operator? owner, decimal power, ConnectorStandard standard, EvseStatus? status, double hoursAgo)
            {
                var evse = Evse.WithEvseId(EvseId.Parse(evseId), location.Id, Now.AddDays(-5));
                evse.OperatorId = owner?.Id;
                evse.MaxPowerKw = power;
                evse.AddConnector(standard, ConnectorFormat.CABLE, PowerType.DC);
                db.Evses.Add(evse);
                if (status is { } value)
                {
                    db.EvseStatusCurrent.Add(EvseStatusCurrent.Create(evse.Id, value, "econtrol", Now.AddHours(-hoursAgo)));
                }

                return evse;
            }

            var first = Add("AT*EVN*E2453*1", stPoelten, evn, 22, ConnectorStandard.IEC_62196_T2, EvseStatus.OUTOFORDER, 30);
            first.ChargingStationId = station.Id;
            Add("AT*EVN*E2453*2", stPoelten, evn, 22, ConnectorStandard.IEC_62196_T2, EvseStatus.OUTOFORDER, 2).ChargingStationId = station.Id;
            Add("AT*EVN*E9*1", vienna, evn, 150, ConnectorStandard.IEC_62196_T2_COMBO, EvseStatus.AVAILABLE, 5);
            Add("AT*SMA*E1*1", vienna, smatrics, 50, ConnectorStandard.CHADEMO, EvseStatus.INOPERATIVE, 48);
            Add("DE*EVN*E1*1", vienna, null, 11, ConnectorStandard.IEC_62196_T2, null, 0);
            Add("AT*EVN*E2453*3", stPoelten, evn, 22, ConnectorStandard.IEC_62196_T2, EvseStatus.OUTOFORDER, 40).Lifecycle = Lifecycle.REMOVED;
            Add("AT*EVN*E3*1", gone, evn, 22, ConnectorStandard.IEC_62196_T2, EvseStatus.OUTOFORDER, 40);
            db.Evses.Add(Evse.WithSurrogate(vienna.Id, Now.AddDays(-5)));
            db.Sources.Add(Source.Create("econtrol", "E-Control", SourceTier.OFFICIAL, "Terms", "Datenquelle: E-Control"));
            db.SourceRecords.Add(SourceRecord.Create("econtrol", "AT*EVN*E2453*1", EntityType.EVSE, first.Id, MatchMethod.CREATED, 1, Now.AddDays(-5)));
            return await db.SaveChangesAsync();
        });

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpClient> ClientAsync()
    {
        if (_token is null)
        {
            var email = $"evse-search-{Guid.NewGuid():N}@chargeglobe.test";
            await factory.CreateConfirmedUserAsync(email);
            _token = (await factory.LoginAsync(email)).AccessToken;
        }

        return factory.Client(_token);
    }

    private async Task<JsonElement> SearchAsync(string query)
    {
        using var client = await ClientAsync();
        var response = await client.GetAsync($"/api/v1/evses?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    private async Task<string[]> IdsAsync(string query) =>
        [.. (await SearchAsync(query)).GetProperty("data").EnumerateArray().Select(item => item.GetProperty("evseId").GetString()!)];

    [Fact]
    public async Task Every_filter_narrows_the_active_EVSEs()
    {
        var all = await IdsAsync(string.Empty);
        Assert.Equal(6, all.Length);
        Assert.Equal(["AT*EVN*E2453*1", "AT*EVN*E2453*2", "AT*EVN*E9*1", "AT*SMA*E1*1", "DE*EVN*E1*1"], all[..5]);
        Assert.StartsWith("XX*CG*E", all[5], StringComparison.Ordinal);

        Assert.Equal(["AT*EVN*E2453*1", "AT*EVN*E2453*2", "AT*EVN*E9*1", "DE*EVN*E1*1"], await IdsAsync("operatorId=evn"));
        Assert.Equal(["AT*EVN*E2453*1", "AT*EVN*E2453*2", "AT*EVN*E9*1"], await IdsAsync("operatorId=AT*EVN"));
        Assert.Equal(["AT*EVN*E2453*1", "AT*EVN*E2453*2", "AT*EVN*E9*1"], await IdsAsync("operatorId=AT-EVN"));
        Assert.Equal(["AT*EVN*E2453*1", "AT*EVN*E2453*2"], await IdsAsync("region=at-3"));
        Assert.Equal(["AT*EVN*E2453*1", "AT*EVN*E2453*2", "AT*SMA*E1*1"], await IdsAsync("status=OUTOFORDER,inoperative"));
        Assert.Equal(["AT*EVN*E2453*1", "AT*EVN*E2453*2", "AT*SMA*E1*1"], await IdsAsync("status=OUTOFORDER&status=INOPERATIVE"));
        Assert.Equal(2, (await IdsAsync("status=UNKNOWN")).Length);
        Assert.Equal(["AT*EVN*E9*1", "AT*SMA*E1*1"], await IdsAsync("minPowerKw=50"));
        Assert.Equal(["AT*EVN*E2453*1", "AT*EVN*E2453*2", "DE*EVN*E1*1"], await IdsAsync("maxPowerKw=22"));
        Assert.Equal(["AT*SMA*E1*1"], await IdsAsync("connector=CHADEMO"));
        Assert.Equal(["AT*EVN*E2453*1"], await IdsAsync("operatorId=EVN&status=OUTOFORDER&statusOlderThan=PT24H"));
        Assert.Equal(["AT*EVN*E2453*1", "AT*SMA*E1*1"], await IdsAsync("statusOlderThan=P1D"));
        Assert.Equal(["AT*EVN*E2453*1", "AT*EVN*E2453*2"], await IdsAsync("chargingStation=AT*EVN*E2453"));
        Assert.Equal(["AT*EVN*E2453*1", "AT*EVN*E2453*2"], await IdsAsync("chargingStation=at-evn-e2453"));
        Assert.Equal(["AT*EVN*E2453*1", "AT*EVN*E2453*2"], await IdsAsync($"chargingStation={_station}"));
        Assert.Empty(await IdsAsync("chargingStation=AT*EVN*E9999"));
    }

    [Fact]
    public async Task An_EVSE_carries_location_station_status_and_the_attribution()
    {
        var body = await SearchAsync("chargingStation=AT*EVN*E2453&limit=1");
        var evse = body.GetProperty("data").EnumerateArray().Single();
        Assert.Equal("AT*EVN*E2453*1", evse.GetProperty("evseId").GetString());
        var location = evse.GetProperty("location");
        Assert.Equal(("Rathausplatz", "St. Pölten", "AT-3"), (
            location.GetProperty("name").GetString(), location.GetProperty("city").GetString(), location.GetProperty("region").GetString()));
        Assert.Equal(JsonValueKind.Null, location.GetProperty("parkId").ValueKind);
        Assert.Equal((_station, "AT*EVN*E2453"), (
            evse.GetProperty("chargingStation").GetProperty("id").GetGuid(), evse.GetProperty("chargingStation").GetProperty("label").GetString()));
        Assert.Equal(("OUTOFORDER", "econtrol", 22m), (
            evse.GetProperty("status").GetString(), evse.GetProperty("statusSource").GetString(), evse.GetProperty("maxPowerKw").GetDecimal()));
        Assert.InRange(evse.GetProperty("statusSince").GetDateTimeOffset(), Now.AddHours(-30).AddSeconds(-1), Now.AddHours(-30).AddSeconds(1));
        var meta = body.GetProperty("meta");
        Assert.Equal((1, 1), (meta.GetProperty("returned").GetInt32(), meta.GetProperty("limit").GetInt32()));
        Assert.Equal(JsonValueKind.String, meta.GetProperty("nextCursor").ValueKind);
        Assert.Equal("econtrol", meta.GetProperty("attribution").EnumerateArray().Single().GetProperty("source").GetString());

        var unknown = (await SearchAsync("operatorId=DE*EVN")).GetProperty("data").EnumerateArray().Single();
        Assert.Equal(("UNKNOWN", JsonValueKind.Null, JsonValueKind.Null, JsonValueKind.Null), (
            unknown.GetProperty("status").GetString(), unknown.GetProperty("statusSince").ValueKind,
            unknown.GetProperty("statusSource").ValueKind, unknown.GetProperty("chargingStation").ValueKind));
    }

    [Theory]
    [InlineData("evseId")]
    [InlineData("statusSince")]
    public async Task Pages_follow_each_other_without_gaps_or_repeats(string sort)
    {
        var expected = await IdsAsync($"sort={sort}&limit=100");
        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = await SearchAsync($"sort={sort}&limit=2" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}"));
            seen.AddRange(page.GetProperty("data").EnumerateArray().Select(item => item.GetProperty("evseId").GetString()!));
            cursor = page.GetProperty("meta").GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        Assert.Equal(expected, seen);
        if (sort == "statusSince")
        {
            // Longest unchanged status first, EVSEs without status last.
            Assert.Equal(["AT*SMA*E1*1", "AT*EVN*E2453*1", "AT*EVN*E9*1", "AT*EVN*E2453*2"], expected[..4]);
        }
    }

    [Theory]
    [InlineData("limit=0", "limit")]
    [InlineData("limit=1001", "limit")]
    [InlineData("operatorId=EVNX", "operatorId")]
    [InlineData("operatorId=1TEVN", "operatorId")]
    [InlineData("operatorId=E%C3%84N", "operatorId")]
    [InlineData("region=AT-99", "region")]
    [InlineData("region=Wien", "region")]
    [InlineData("status=BROKEN", "status")]
    [InlineData("status=1", "status")]
    [InlineData("connector=SCHUKO", "connector")]
    [InlineData("minPowerKw=abc", "minPowerKw")]
    [InlineData("maxPowerKw=0", "maxPowerKw")]
    [InlineData("minPowerKw=50&maxPowerKw=22", "maxPowerKw")]
    [InlineData("statusOlderThan=24h", "statusOlderThan")]
    [InlineData("statusOlderThan=PT0S", "statusOlderThan")]
    [InlineData("statusOlderThan=P400D", "statusOlderThan")]
    [InlineData("statusOlderThan=P99999999999999D", "statusOlderThan")]
    [InlineData("chargingStation=%20", "chargingStation")]
    [InlineData("sort=name", "sort")]
    [InlineData("cursor=%21%21%21", "cursor")]
    [InlineData("cursor=bm9uZQ", "cursor")]
    public async Task Invalid_filters_are_rejected(string query, string field)
    {
        using var client = await ClientAsync();
        var problem = await (await client.GetAsync($"/api/v1/evses?{query}")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), problem.GetRawText());
    }

    [Fact]
    public async Task A_cursor_belongs_to_its_sort_and_the_search_needs_evse_read()
    {
        var cursor = (await SearchAsync("sort=evseId&limit=1")).GetProperty("meta").GetProperty("nextCursor").GetString()!;
        using var client = await ClientAsync();
        await (await client.GetAsync($"/api/v1/evses?sort=statusSince&cursor={Uri.EscapeDataString(cursor)}")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        var since = (await SearchAsync("sort=statusSince&limit=1")).GetProperty("meta").GetProperty("nextCursor").GetString()!;
        Assert.Single(await IdsAsync($"sort=statusSince&limit=1&cursor={Uri.EscapeDataString(since)}"));
        // A cursor of the status order with a key that is no time.
        var forged = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"StatusSince|yesterday|{Guid.NewGuid()}"));
        await (await client.GetAsync($"/api/v1/evses?sort=statusSince&cursor={Uri.EscapeDataString(forged)}")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);

        await (await factory.Client().GetAsync("/api/v1/evses")).AssertProblemAsync(HttpStatusCode.Unauthorized);
    }
}
