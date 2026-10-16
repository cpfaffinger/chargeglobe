using ChargeGlobe.Application.Mail;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace ChargeGlobe.Jobs.Tests;

public class MailDeliveryServiceTests
{
    private static async Task WaitUntilAsync(Func<bool> condition, string expectation)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {expectation}");
            await Task.Delay(10);
        }
    }

    private static (MailDeliveryService Service, FakeTimeProvider Time, MessageLogger Logger) Create(StubDispatcher dispatcher)
    {
        var services = new ServiceCollection().AddScoped<IOutboxDispatcher>(_ => dispatcher).BuildServiceProvider();
        var time = new FakeTimeProvider();
        var logger = new MessageLogger();
        return (new MailDeliveryService(services.GetRequiredService<IServiceScopeFactory>(), time, logger), time, logger);
    }

    [Fact]
    public async Task Drains_the_outbox_every_interval_and_purges_once_per_day()
    {
        var dispatcher = new StubDispatcher(10, 3, 0);
        var (service, time, logger) = Create(dispatcher);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => dispatcher.Purges == 1, "first cycle");

        Assert.Equal(3, dispatcher.Dispatches);
        Assert.Contains("Mail outbox: processed 13 mail(s)", logger.Messages);
        Assert.Contains("Mail outbox: retention removed 2 sent mail(s)", logger.Messages);

        time.Advance(MailDeliveryService.Interval);
        await WaitUntilAsync(() => dispatcher.Dispatches == 4, "second cycle");
        Assert.Equal(1, dispatcher.Purges);
        Assert.Single(logger.Messages, message => message.StartsWith("Mail outbox: processed", StringComparison.Ordinal));

        time.Advance(MailDeliveryService.PurgeInterval);
        await WaitUntilAsync(() => dispatcher.Purges == 2, "purge after a day");

        await service.StopAsync(CancellationToken.None);
        var dispatchesAtStop = dispatcher.Dispatches;
        time.Advance(MailDeliveryService.Interval);
        await Task.Delay(50);
        Assert.Equal(dispatchesAtStop, dispatcher.Dispatches);
    }

    [Fact]
    public async Task A_failing_cycle_is_logged_and_the_next_tick_tries_again()
    {
        var dispatcher = new StubDispatcher { FailNext = true };
        var (service, time, logger) = Create(dispatcher);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => logger.Messages.Count == 1, "warning of the failed cycle");
        Assert.Equal(["Mail outbox cycle failed, retrying with the next tick: database is down"], logger.Messages);
        Assert.Equal(0, dispatcher.Purges);

        time.Advance(MailDeliveryService.Interval);
        await WaitUntilAsync(() => dispatcher.Purges == 1, "successful second cycle");

        await service.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(JobRole.Worker, new[] { typeof(HeartbeatService), typeof(MailDeliveryService), typeof(IngestWorkerService), typeof(AddressImportService), typeof(LocationParkService) })]
    [InlineData(JobRole.Crawler, new[] { typeof(HeartbeatService), typeof(ProviderHealthService), typeof(PollScheduler) })]
    public void Mail_delivery_runs_in_the_worker_role_and_provider_probes_in_the_crawler_role(JobRole role, Type[] expected)
    {
        var options = new JobOptions(role, "instance-1");

        var services = new ServiceCollection().AddJobs(options);

        Assert.Equal(
            expected,
            services.Where(service => service.ServiceType == typeof(IHostedService)).Select(service => service.ImplementationType));
        Assert.Same(options, services.BuildServiceProvider().GetRequiredService<JobOptions>());
    }

    private sealed class StubDispatcher(params int[] batches) : IOutboxDispatcher
    {
        private readonly Queue<int> _batches = new(batches);
        private int _dispatches;
        private int _purges;

        public bool FailNext { get; set; }

        public int Dispatches => Volatile.Read(ref _dispatches);

        public int Purges => Volatile.Read(ref _purges);

        public Task<int> DispatchAsync(CancellationToken cancellationToken)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("database is down");
            }

            Interlocked.Increment(ref _dispatches);
            return Task.FromResult(_batches.TryDequeue(out var batch) ? batch : 0);
        }

        public Task<int> PurgeSentAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _purges);
            return Task.FromResult(2);
        }
    }

    private sealed class MessageLogger : ILogger<MailDeliveryService>
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                {
                    return [.. _messages];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_messages)
            {
                _messages.Add(formatter(state, exception));
            }
        }
    }
}
