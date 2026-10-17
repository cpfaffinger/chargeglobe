using ChargeGlobe.Application.Identity;
using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Application.Tariffs;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Providers.Abstractions;
using ChargeGlobe.Providers.TariffCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Catalog = ChargeGlobe.Infrastructure.Persistence.TariffCatalog;

namespace ChargeGlobe.Jobs.Tests;

public class TariffCatalogServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Every_source_is_imported_at_start_and_every_six_hours_by_one_worker()
    {
        var catalog = new CatalogStub();
        catalog.Results["good"] = new CatalogImportResult(2, 3, 4, 1, 1, 5, []);
        catalog.Results["bad"] = new CatalogImportResult(0, 0, 0, 0, 0, 0, ["network 'x': the key is used twice.", "emp 'y': a name is required."]);
        var leases = new Leases();
        var services = new ServiceCollection()
            .AddScoped<IJobLeases>(_ => leases)
            .AddScoped<ITariffCatalog>(_ => catalog)
            .BuildServiceProvider();
        var time = new FakeTimeProvider(T0);
        var logger = new IngestJobsTests.ListLogger<TariffCatalogService>();
        var failing = new Source("failing") { Failure = new InvalidOperationException("catalog file broken") };
        var service = new TariffCatalogService(
            services.GetRequiredService<IServiceScopeFactory>(), [new Source("good"), new Source("bad"), failing], new JobOptions(JobRole.Worker, "worker-1"), time, logger);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => logger.Messages.Count == 3, "first run");
        Assert.Equal(
            [
                "Tariff catalog good: 2 networks, 3 providers; tariffs 4 new, 1 changed, 1 ended, 5 unchanged",
                "Tariff catalog bad not imported, 2 issue(s): network 'x': the key is used twice. | emp 'y': a name is required.",
                "Tariff catalog import failed, retrying with the next tick: catalog file broken",
            ],
            logger.Messages);
        Assert.Equal((TariffCatalogService.JobName, "worker-1"), (leases.Job, leases.Instance));
        Assert.Equal(["good", "bad"], catalog.Imported);

        // Another worker holds the lease: nothing is imported.
        leases.Grant = false;
        time.Advance(TariffCatalogService.Interval);
        await WaitUntilAsync(() => leases.Calls == 2, "lease refused");
        Assert.Equal(2, catalog.Imported.Count);

        leases.Grant = true;
        failing.Failure = null;
        time.Advance(TariffCatalogService.Interval);
        await WaitUntilAsync(() => leases.Releases == 2, "third run");
        Assert.Equal(["good", "bad", "good", "bad", "failing"], catalog.Imported);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_curated_catalog_is_valid_and_registered_as_tariff_source()
    {
        var source = Assert.Single(new ServiceCollection().AddCuratedTariffCatalog().BuildServiceProvider().GetServices<ITariffSource>());
        Assert.Equal(CuratedTariffCatalog.SourceKey, source.Key);

        var snapshot = await source.FetchAsync(CancellationToken.None);

        Assert.Empty(Catalog.Validate(snapshot, []));
        Assert.Contains("at/networks.json", CuratedTariffCatalog.Files());
        Assert.True(snapshot.Emps.Count >= 20, $"{snapshot.Emps.Count} providers");
        // One file per provider, named by its key.
        Assert.All(CuratedTariffCatalog.Files().Where(file => file != "at/networks.json"), file => Assert.Equal($"at/{Assert.Single(CuratedTariffCatalog.Read(file).Emps).Key}.json", file));
        Assert.All(snapshot.Emps.SelectMany(emp => emp.Tariffs), tariff => Assert.StartsWith("https://", tariff.SourceUrl));
    }

    [Fact]
    public async Task The_curated_catalog_prices_the_home_network_below_partners_and_roaming()
    {
        var snapshot = await new CuratedTariffCatalog().FetchAsync(CancellationToken.None);
        var networks = snapshot.Networks.ToDictionary(network => network.Key, network => (IReadOnlyCollection<string>)network.Operators);

        TariffMatch? Match(string emp, string tariff, string operatorId, TariffCurrent current = TariffCurrent.AC, decimal power = 11)
        {
            var record = snapshot.Emps.Single(item => item.Key == emp).Tariffs.Single(item => item.Key == tariff);
            var plan = TariffPlan.Create(Guid.CreateVersion7(), CuratedTariffCatalog.SourceKey, Catalog.ContentOf(record), "hash", T0);
            return plan.MatchAt(operatorId, true, current, power, key => networks.GetValueOrDefault(key) ?? []);
        }

        static (TariffZoneKind, decimal?) Of(TariffMatch? match) => (match!.Zone.Kind, match.Price!.PricePerKwh);

        // EVN: home network, the dearer partner network of every other operator, not at Tesla; Drive Easy only at EVN.
        Assert.Equal((TariffZoneKind.HOME, 0.45m), Of(Match("evn", "drive-smart", "AT*EVN")));
        Assert.Equal((TariffZoneKind.HOME, 0.49m), Of(Match("evn", "drive-smart", "AT*EVN", TariffCurrent.DC, 300)));
        Assert.Equal((TariffZoneKind.ROAMING, 0.54m), Of(Match("evn", "drive-smart", "AT*VNA")));
        Assert.Null(Match("evn", "drive-smart", "AT*TSL", TariffCurrent.DC, 250));
        Assert.Null(Match("evn", "drive-easy", "AT*VIE"));
        // Wien Energie: all three operator IDs are the home network.
        Assert.Equal((TariffZoneKind.HOME, 0.43m), Of(Match("wien-energie", "tanke-kwh-go", "AT*VES")));
        // ÖAMTC: listed partners only.
        Assert.Equal((TariffZoneKind.PARTNER, 0.74m), Of(Match("oeamtc", "epower", "AT*IOY", TariffCurrent.DC, 350)));
        Assert.Null(Match("oeamtc", "epower", "AT*ALL", TariffCurrent.DC, 300));
        // TIWAG: a roaming price per operator, none for unlisted ones.
        Assert.Equal((TariffZoneKind.ROAMING, 0.54m), Of(Match("tiwag", "tiwag-mobil", "AT*120")));
        Assert.Equal((TariffZoneKind.ROAMING, 0.90m), Of(Match("tiwag", "tiwag-mobil", "AT*ENA", TariffCurrent.DC, 150)));
        Assert.Null(Match("tiwag", "tiwag-mobil", "AT*EGG"));
        // Salzburg AG: the PLUS partners per kWh before the per-minute roaming.
        Assert.Equal((TariffZoneKind.ROAMING, 0.89m), Of(Match("salzburg-ag", "stromladen-easy", "AT*IOY", TariffCurrent.DC, 300)));
        Assert.Equal(0.931m, Match("salzburg-ag", "stromladen-easy", "AT*EVN", TariffCurrent.DC, 150)!.Price!.PricePerMinute);
        // SMATRICS and VERBUND share the network; Elli prices IONITY as a single operator.
        Assert.Equal((TariffZoneKind.HOME, 0.49m), Of(Match("verbund", "v-go-basic", "AT*SME", TariffCurrent.DC, 150)));
        // Providers passing on the operator's price: no price element, the markup of the zone.
        var electroverse = Match("octopus-electroverse", "electroverse", "AT*EVN")!;
        Assert.Equal((TariffPricing.OPERATOR_AD_HOC, null), (electroverse.Zone.Pricing, electroverse.Price));
        Assert.Equal(10m, Match("chargemap", "pass", "AT*VIE")!.Zone.MarkupPercent);
        Assert.Equal((TariffZoneKind.OPERATOR, 0.49m), Of(Match("mercedes-charge", "public-m", "AT*IOY", TariffCurrent.DC, 350)));
        Assert.Equal(-10m, Match("mercedes-charge", "public-m", "AT*LAG", TariffCurrent.DC, 150)!.Zone.MarkupPercent);
        // EnBW: its price at SMATRICS EnBW, elsewhere a price per charge point that is not published.
        Assert.Equal((TariffZoneKind.HOME, 0.56m), Of(Match("enbw", "ladetarif-s", "AT*SME", TariffCurrent.DC, 150)));
        Assert.Equal(TariffPricing.UNKNOWN, Match("enbw", "ladetarif-s", "AT*EVN")!.Zone.Pricing);
        // Energie Graz with both operator IDs, the session fee of Kia on the operator's price.
        Assert.Equal((TariffZoneKind.HOME, 0.53m), Of(Match("energie-graz", "charge-public", "AT*6D6")));
        Assert.Equal(0.59m, Match("kia-charge", "easy", "AT*VIE")!.Zone.MarkupSessionFee);
        Assert.Equal((TariffZoneKind.OPERATOR, 0.46m), Of(Match("elli", "premium", "AT*IOY", TariffCurrent.DC, 350)));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string expectation)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {expectation}");
            await Task.Delay(10);
        }
    }

    private sealed class Source(string key) : ITariffSource
    {
        public Exception? Failure { get; set; }

        public string Key => key;

        public Task<TariffCatalogSnapshot> FetchAsync(CancellationToken cancellationToken) =>
            Failure is null ? Task.FromResult(new TariffCatalogSnapshot([], [])) : Task.FromException<TariffCatalogSnapshot>(Failure);
    }

    private sealed class CatalogStub : ITariffCatalog
    {
        private readonly List<string> _imported = [];

        public Dictionary<string, CatalogImportResult> Results { get; } = [];

        public IReadOnlyList<string> Imported
        {
            get
            {
                lock (_imported)
                {
                    return [.. _imported];
                }
            }
        }

        public Task<IReadOnlyList<EmpView>> EmpsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<NetworkView>> NetworksAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<TariffView>> TariffsAsync(string? emp, bool history, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CatalogImportResult> ImportAsync(string sourceKey, TariffCatalogSnapshot snapshot, Actor? actor, CancellationToken cancellationToken)
        {
            lock (_imported)
            {
                _imported.Add(sourceKey);
            }

            return Task.FromResult(Results.GetValueOrDefault(sourceKey) ?? new CatalogImportResult(0, 0, 0, 0, 0, 0, []));
        }
    }

    private sealed class Leases : IJobLeases
    {
        private int _calls;
        private int _releases;

        public bool Grant { get; set; } = true;

        public int Calls => Volatile.Read(ref _calls);

        public int Releases => Volatile.Read(ref _releases);

        public string? Job { get; private set; }

        public string? Instance { get; private set; }

        public Task<bool> TryAcquireAsync(string jobName, string instanceId, TimeSpan duration, CancellationToken cancellationToken)
        {
            (Job, Instance) = (jobName, instanceId);
            Interlocked.Increment(ref _calls);
            return Task.FromResult(Grant);
        }

        public Task ReleaseAsync(string jobName, string instanceId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _releases);
            return Task.CompletedTask;
        }
    }
}
