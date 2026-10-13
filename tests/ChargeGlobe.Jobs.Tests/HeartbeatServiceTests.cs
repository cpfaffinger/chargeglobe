using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace ChargeGlobe.Jobs.Tests;

public class HeartbeatServiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Logs_a_heartbeat_at_startup_and_after_every_interval()
    {
        var timeProvider = new FakeTimeProvider();
        var logger = new RecordingLogger();
        using var service = new HeartbeatService(
            new JobOptions(JobRole.Crawler, "instance-1"), timeProvider, logger);

        await service.StartAsync(CancellationToken.None);
        await logger.WaitForMessagesAsync(1, Timeout);

        timeProvider.Advance(HeartbeatService.Interval);
        await logger.WaitForMessagesAsync(2, Timeout);

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(
            ["Heartbeat role=crawler instance=instance-1", "Heartbeat role=crawler instance=instance-1"],
            logger.Messages);
    }

    private sealed class RecordingLogger : ILogger<HeartbeatService>
    {
        private readonly List<string> _messages = [];
        private readonly SemaphoreSlim _signal = new(0);

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

        public async Task WaitForMessagesAsync(int count, TimeSpan timeout)
        {
            while (Messages.Count < count)
            {
                Assert.True(await _signal.WaitAsync(timeout), $"Expected {count} heartbeat(s).");
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_messages)
            {
                _messages.Add(formatter(state, exception));
            }

            _signal.Release();
        }
    }
}
