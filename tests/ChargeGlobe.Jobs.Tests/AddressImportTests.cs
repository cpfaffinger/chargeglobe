using System.Runtime.CompilerServices;
using System.Text.Json;
using ChargeGlobe.Application.Addresses;
using ChargeGlobe.Application.Identity;
using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Application.Sources;
using ChargeGlobe.Domain;
using ChargeGlobe.Infrastructure.Addresses;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace ChargeGlobe.Jobs.Tests;

/// <summary>The import of the BEV address register: when it runs, what it replaces, and the worker job around it.</summary>
public class AddressImportTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly October = new(2026, 10, 1);
    private static readonly DateOnly April = new(2026, 4, 1);

    private static readonly AddressRecord Stephansplatz =
        new(6793782, "Stephansplatz", "1", "1010", "Wien,Innere Stadt", "Wien", "90001", 3007.82, 341148.29, 31256);

    private static (AddressImporter Importer, FakeRegister Register, FakeStore Store, FakeSources Sources) Create(AddressImportState? state, DateOnly? online)
    {
        var (register, store, sources) = (new FakeRegister(online), new FakeStore(state), new FakeSources());
        return (new AddressImporter(register, store, sources, new Config(), new FakeTimeProvider(T0)), register, store, sources);
    }

    [Fact]
    public async Task A_newer_release_replaces_all_addresses_and_names_its_reference_date_in_the_attribution()
    {
        var (importer, register, store, sources) = Create(new AddressImportState(April, T0.AddDays(-200), T0.AddDays(-31), 10), October);

        var result = await importer.RunAsync(CancellationToken.None);

        Assert.Equal(new AddressImportResult(AddressImportOutcome.Imported, October, 1), result);
        Assert.Equal([Stephansplatz], store.Replaced);
        Assert.Equal((October, T0), (store.ReferenceDate, store.At));
        Assert.True(register.Disposed);
        var source = Assert.Single(sources.Synced);
        Assert.Equal(("bev-address", "CC-BY-4.0", "© Österreichisches Adressregister, Stichtagsdaten vom 01.10.2026"), (source.Key, source.License, source.AttributionText));
    }

    [Fact]
    public async Task The_first_import_needs_no_earlier_state()
    {
        var (importer, _, store, _) = Create(null, October);

        Assert.Equal(AddressImportOutcome.Imported, (await importer.RunAsync(CancellationToken.None)).Outcome);
        Assert.Equal([Stephansplatz], store.Replaced);
    }

    [Fact]
    public async Task The_register_is_checked_only_every_refresh_days()
    {
        var (importer, register, store, _) = Create(new AddressImportState(April, T0.AddDays(-200), T0.AddDays(-29), 10), October);

        Assert.Equal(new AddressImportResult(AddressImportOutcome.NotDue, April, 10), await importer.RunAsync(CancellationToken.None));
        Assert.Equal(0, register.Probes);
        Assert.Null(store.Replaced);
    }

    [Fact]
    public async Task The_same_reference_date_is_only_marked_as_checked()
    {
        var (importer, _, store, sources) = Create(new AddressImportState(October, T0.AddDays(-15), T0.AddDays(-30), 10), October);

        Assert.Equal(new AddressImportResult(AddressImportOutcome.UpToDate, October, 10), await importer.RunAsync(CancellationToken.None));
        Assert.Equal(T0, store.CheckedAt);
        Assert.Null(store.Replaced);
        Assert.Empty(sources.Synced);
    }

    [Fact]
    public async Task Without_a_release_online_nothing_changes()
    {
        var (importer, _, store, _) = Create(new AddressImportState(April, T0.AddDays(-200), T0.AddDays(-31), 10), null);
        Assert.Equal(new AddressImportResult(AddressImportOutcome.NoRelease, April, 10), await importer.RunAsync(CancellationToken.None));
        Assert.Null(store.CheckedAt);

        var (first, _, _, _) = Create(null, null);
        Assert.Equal(new AddressImportResult(AddressImportOutcome.NoRelease, null, 0), await first.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_worker_job_runs_hourly_under_a_lease_and_logs_the_outcome()
    {
        var store = new FakeStore(null);
        var register = new FakeRegister(October);
        var leases = new Leases();
        var services = new ServiceCollection()
            .AddScoped<IJobLeases>(_ => leases)
            .AddScoped<IAddressStore>(_ => store)
            .AddScoped<IAddressRegister>(_ => register)
            .AddScoped<ISourceRegistry, FakeSources>()
            .AddSingleton<IAppConfig, Config>()
            .AddSingleton<TimeProvider>(new FakeTimeProvider(T0))
            .AddScoped<AddressImporter>()
            .BuildServiceProvider();
        var time = new FakeTimeProvider(T0);
        var logger = new IngestJobsTests.ListLogger<AddressImportService>();
        var service = new AddressImportService(services.GetRequiredService<IServiceScopeFactory>(), new JobOptions(JobRole.Worker, "worker-1"), time, logger);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => logger.Messages.Any(message => message.StartsWith("Address import: 1 addresses of the reference date 2026-10-01 in ", StringComparison.Ordinal)), "import");
        Assert.Equal((AddressImporter.JobName, "worker-1", AddressImportService.LeaseDuration), (leases.Job, leases.Instance, leases.Duration));

        // An hour later the check is not due: no message.
        store.State = new AddressImportState(October, T0, T0, 1);
        time.Advance(AddressImportService.Interval);
        await WaitUntilAsync(() => leases.Calls == 2, "second tick");

        store.State = new AddressImportState(October, T0, T0.AddDays(-40), 1);
        time.Advance(AddressImportService.Interval);
        await WaitUntilAsync(() => logger.Messages.Contains("Address import: reference date 2026-10-01 is the newest (1 addresses)"), "up to date");

        store.State = store.State with { CheckedAt = T0.AddDays(-40) };
        register.Online = null;
        time.Advance(AddressImportService.Interval);
        await WaitUntilAsync(() => logger.Messages.Contains("Address import: no release of the address register found, retrying with the next tick"), "no release");

        // Another worker holds the lease: the register is not asked.
        leases.Grant = false;
        var probes = register.Probes;
        time.Advance(AddressImportService.Interval);
        await WaitUntilAsync(() => leases.Calls == 5, "lease refused");
        Assert.Equal(probes, register.Probes);

        leases.Grant = true;
        register.Failure = new HttpRequestException("register offline");
        time.Advance(AddressImportService.Interval);
        await WaitUntilAsync(() => logger.Messages.Contains("Address import failed, retrying with the next tick: register offline"), "failure");
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void The_worker_role_registers_the_import_with_the_adapter_of_the_BEV()
    {
        var services = new ServiceCollection()
            .AddJobs(new JobOptions(JobRole.Worker, "worker-1"))
            .AddSingleton<IAppConfig, Config>()
            .AddSingleton(TimeProvider.System);

        using var provider = services.BuildServiceProvider();
        Assert.IsType<BevAddressRegister>(provider.GetRequiredService<IAddressRegister>());
        Assert.Contains(services, service => service.ImplementationType == typeof(AddressImportService));
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

    private sealed class Config : IAppConfig
    {
        public T Get<T>(string key, string scope = SettingDefinition.GlobalScope) => (T)SettingsCatalog.Find(key)!.Default!;

        public Task SetAsync(string key, string scope, JsonElement? value, Actor actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeRegister(DateOnly? online) : IAddressRegister
    {
        private int _probes;

        public DateOnly? Online { get; set; } = online;

        public Exception? Failure { get; set; }

        public int Probes => Volatile.Read(ref _probes);

        public bool Disposed { get; private set; }

        public Task<AddressRegisterRelease?> FindNewestAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _probes);
            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.FromResult(Online is { } date ? new AddressRegisterRelease(date, new Uri("https://bev.test/a.zip")) : null);
        }

        public Task<IAddressRegisterFile> OpenAsync(AddressRegisterRelease release, CancellationToken cancellationToken) =>
            Task.FromResult<IAddressRegisterFile>(new RegisterFile(release.ReferenceDate, () => Disposed = true));

        private sealed class RegisterFile(DateOnly referenceDate, Action disposed) : IAddressRegisterFile
        {
            public DateOnly ReferenceDate => referenceDate;

            public async IAsyncEnumerable<AddressRecord> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
            {
                await Task.Yield();
                yield return Stephansplatz;
            }

            public ValueTask DisposeAsync()
            {
                disposed();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeStore(AddressImportState? state) : IAddressStore
    {
        public AddressImportState? State { get; set; } = state;

        public List<AddressRecord>? Replaced { get; private set; }

        public DateOnly? ReferenceDate { get; private set; }

        public DateTimeOffset? At { get; private set; }

        public DateTimeOffset? CheckedAt { get; private set; }

        public Task<AddressImportState?> StateAsync(CancellationToken cancellationToken) => Task.FromResult(State);

        public Task MarkCheckedAsync(DateTimeOffset at, CancellationToken cancellationToken)
        {
            CheckedAt = at;
            return Task.CompletedTask;
        }

        public async Task<int> ReplaceAsync(IAsyncEnumerable<AddressRecord> records, DateOnly referenceDate, DateTimeOffset at, CancellationToken cancellationToken)
        {
            Replaced = [];
            await foreach (var record in records)
            {
                Replaced.Add(record);
            }

            (ReferenceDate, At) = (referenceDate, at);
            return Replaced.Count;
        }

        public Task<AddressSearchResult> SearchAsync(string query, GeoPoint? near, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeSources : ISourceRegistry
    {
        public List<SourceDescription> Synced { get; } = [];

        public Task SyncAsync(IReadOnlyCollection<SourceDescription> sources, CancellationToken cancellationToken)
        {
            Synced.AddRange(sources);
            return Task.CompletedTask;
        }
    }

    private sealed class Leases : IJobLeases
    {
        private int _calls;

        public bool Grant { get; set; } = true;

        public int Calls => Volatile.Read(ref _calls);

        public string? Job { get; private set; }

        public string? Instance { get; private set; }

        public TimeSpan Duration { get; private set; }

        public Task<bool> TryAcquireAsync(string jobName, string instanceId, TimeSpan duration, CancellationToken cancellationToken)
        {
            (Job, Instance, Duration) = (jobName, instanceId, duration);
            Interlocked.Increment(ref _calls);
            return Task.FromResult(Grant);
        }
    }
}
