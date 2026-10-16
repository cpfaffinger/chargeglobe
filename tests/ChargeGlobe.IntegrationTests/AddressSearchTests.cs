using System.Net;
using System.Runtime.CompilerServices;
using ChargeGlobe.Application.Addresses;
using ChargeGlobe.Application.Sources;
using ChargeGlobe.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChargeGlobe.IntegrationTests;

/// <summary>Table <c>address</c>: replacement by staging table and swap, the search behind GET /geocode and the endpoint.</summary>
[Collection(IdentityCollection.Name)]
public class AddressSearchTests(IntegrationFactory factory)
{
    private static readonly DateOnly October = new(2026, 10, 1);
    private static readonly DateTimeOffset ImportedAt = new(2026, 10, 16, 8, 0, 0, TimeSpan.Zero);
    private static readonly GeoPoint Vienna = new(48.2085, 16.3731);
    private static readonly GeoPoint Pamhagen = new(47.70, 16.90);
    private static readonly GeoPoint Graz = new(47.07, 15.44);

    /// <summary>Positions in the projections of the register (MGI / Austria GK West, Central, East).</summary>
    private static readonly AddressRecord[] Addresses =
    [
        new(6793782, "Stephansplatz", "1", "1010", "Wien,Innere Stadt", "Wien", "90001", 3007.82, 341148.29, 31256),
        new(6793790, "Stephansplatz", "10", "1010", "Wien,Innere Stadt", "Wien", "90001", 3050.00, 341100.00, 31256),
        // The same code again: the first one wins.
        new(6793790, "Stephansplatz", "10", "1010", "Wien,Innere Stadt", "Wien", "90001", 0, 0, 31256),
        new(7000001, "Stephansplatz", "1", "7152", "Pamhagen", "Pamhagen", "10716", 42_500, 284_000, 31256),
        new(5268477, "Hauptplatz", "1", "4020", "Linz", "Linz", "40101", 70793.70, 352412.69, 31255),
        new(5268486, "Hauptplatz", "10", "4020", "Linz", "Linz", "40101", 70800.00, 352400.00, 31255),
        new(8000001, "Hauptplatz", "1", "8010", "Graz,Innere Stadt", "Graz", "60101", 159_600, 215_000, 31255),
        new(9000001, "Mariahilfer Straße", "1A", "1060", "Wien,Mariahilf", "Wien", "90001", 2300, 340500, 31256),
        new(9000002, "Dorfstraße", "3", "6020", "Innsbruck", "Innsbruck", "70101", 76_000, 237_000, 31254),
    ];

    private Task<T> WithStoreAsync<T>(Func<IAddressStore, Task<T>> action) =>
        factory.WithScopeAsync(services => action(services.GetRequiredService<IAddressStore>()));

    private Task<int> ReplaceAsync(params AddressRecord[] records) =>
        WithStoreAsync(store => store.ReplaceAsync(Stream(records), October, ImportedAt, CancellationToken.None));

    private static async IAsyncEnumerable<AddressRecord> Stream(AddressRecord[] records, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var record in records)
        {
            await Task.Yield();
            yield return record;
        }
    }

    private Task<AddressSearchResult> SearchAsync(string query, GeoPoint? near = null) =>
        WithStoreAsync(store => store.SearchAsync(query, near, 8, CancellationToken.None));

    private Task SyncSourceAsync() =>
        factory.WithScopeAsync(async services =>
        {
            await services.GetRequiredService<ISourceRegistry>().SyncAsync([AddressImporter.Source(October)], CancellationToken.None);
            return true;
        });

    [Fact]
    public async Task A_replacement_swaps_the_whole_table_and_records_the_reference_date()
    {
        // A smaller first load, then the full one: nothing of the first stays.
        Assert.Equal(1, await ReplaceAsync(Addresses[^1]));
        Assert.Equal(Addresses.Length - 1, await ReplaceAsync(Addresses));

        var state = await WithStoreAsync(store => store.StateAsync(CancellationToken.None));
        Assert.Equal(new AddressImportState(October, ImportedAt, ImportedAt, Addresses.Length - 1), state);
        await WithStoreAsync(async store =>
        {
            await store.MarkCheckedAsync(ImportedAt.AddDays(30), CancellationToken.None);
            return true;
        });
        Assert.Equal(ImportedAt.AddDays(30), (await WithStoreAsync(store => store.StateAsync(CancellationToken.None)))!.CheckedAt);

        var indexes = await factory.WithDbAsync(db => db.Database
            .SqlQuery<string>($"SELECT indexname AS \"Value\" FROM pg_indexes WHERE tablename = 'address' ORDER BY 1")
            .ToListAsync());
        Assert.Equal(["ix_address_search", "pk_address"], indexes);
        var leftovers = await factory.WithDbAsync(db => db.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_tables WHERE tablename IN ('address_new', 'address_load')")
            .SingleAsync());
        Assert.Equal(0, leftovers);

        var stephansplatz = (await SearchAsync("Stephansplatz 1 Wien")).Hits[0];
        Assert.Equal("Stephansplatz 1, 1010 Wien", stephansplatz.Label);
        Assert.InRange(stephansplatz.Latitude, 48.207, 48.210);
        Assert.InRange(stephansplatz.Longitude, 16.371, 16.374);
        Assert.InRange((await SearchAsync("Stephansplatz 10 Wien")).Hits[0].Latitude, 48.2, 48.21);
    }

    [Fact]
    public async Task The_exact_house_number_comes_first_then_the_nearest_then_the_most_similar()
    {
        await ReplaceAsync(Addresses);

        Assert.Equal("Hauptplatz 1, 4020 Linz", (await SearchAsync("Hauptplatz 1 Linz")).Hits[0].Label);
        Assert.Equal(["Stephansplatz 1, 1010 Wien", "Stephansplatz 10, 1010 Wien"], (await SearchAsync("Stephansplatz 1 Wien")).Hits.Select(hit => hit.Label));
        Assert.Equal("Stephansplatz 1, 7152 Pamhagen", (await SearchAsync("stephansplatz 1", Pamhagen)).Hits[0].Label);
        Assert.Equal("Stephansplatz 1, 1010 Wien", (await SearchAsync("stephansplatz 1", Vienna)).Hits[0].Label);
        Assert.Equal("Hauptplatz 1, 8010 Graz", (await SearchAsync("Hauptplatz", Graz)).Hits[0].Label);

        // ß as ss, abbreviations and punctuation.
        Assert.Equal("Mariahilfer Straße 1A, 1060 Wien", Assert.Single((await SearchAsync("mariahilfer strasse 1a")).Hits).Label);
        Assert.Single((await SearchAsync("Mariahilfer Str. 1A, Wien")).Hits);
        Assert.Single((await SearchAsync("Dorfstr 3 Innsbruck")).Hits);
    }

    [Fact]
    public async Task Queries_without_a_word_of_three_characters_or_without_hits_find_nothing_and_name_no_source()
    {
        await ReplaceAsync(Addresses);
        await SyncSourceAsync();

        var short1 = await SearchAsync("A 1");
        Assert.Empty(short1.Hits);
        Assert.Empty(short1.Attribution);
        var none = await SearchAsync("Nirgendwogasse 99");
        Assert.Empty(none.Hits);
        Assert.Empty(none.Attribution);
        var attribution = Assert.Single((await SearchAsync("Hauptplatz 1 Linz")).Attribution);
        Assert.Equal(("bev-address", "© Österreichisches Adressregister, Stichtagsdaten vom 01.10.2026", "CC-BY-4.0"), (attribution.Source, attribution.Text, attribution.License));
    }

    [Fact]
    public async Task The_endpoint_needs_route_read_and_answers_at_most_eight_addresses_with_attribution()
    {
        await ReplaceAsync(Addresses);
        await SyncSourceAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.Client().GetAsync("/api/v1/geocode?q=Hauptplatz")).StatusCode);

        var email = $"geocode-{Guid.NewGuid():N}@chargeglobe.test";
        await factory.CreateConfirmedUserAsync(email);
        var client = factory.Client((await factory.LoginAsync(email)).AccessToken);

        var response = await client.GetAsync("/api/v1/geocode?q=Stephansplatz%201&near=47.70,16.90");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadJsonAsync();
        var first = body.GetProperty("data")[0];
        Assert.Equal("Stephansplatz 1, 7152 Pamhagen", first.GetProperty("label").GetString());
        Assert.True(first.TryGetProperty("latitude", out _) && first.TryGetProperty("longitude", out _));
        Assert.Equal(3, body.GetProperty("meta").GetProperty("returned").GetInt32());
        Assert.Equal("bev-address", body.GetProperty("meta").GetProperty("attribution")[0].GetProperty("source").GetString());

        var short1 = await (await client.GetAsync("/api/v1/geocode?q=ab")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.True(short1.GetProperty("errors").TryGetProperty("q", out _));
        await (await client.GetAsync("/api/v1/geocode")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        foreach (var near in new[] { "x", "48.2", "91,16.3", "48.2,181", "a,16.3" })
        {
            var problem = await (await client.GetAsync($"/api/v1/geocode?q=Hauptplatz&near={near}")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
            Assert.True(problem.GetProperty("errors").TryGetProperty("near", out _), near);
        }
    }
}
