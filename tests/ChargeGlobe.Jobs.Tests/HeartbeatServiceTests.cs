using System.Text.Json;
using ChargeGlobe.Application.Identity;
using ChargeGlobe.Application.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace ChargeGlobe.Jobs.Tests;

public class HeartbeatServiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Logs_a_heartbeat_with_the_current_mail_setting_at_startup_and_after_every_interval()
    {
        var timeProvider = new FakeTimeProvider();
        var logger = new RecordingLogger();
        var config = new StubConfig();
        using var service = new HeartbeatService(
            new JobOptions(JobRole.Crawler, "instance-1"), timeProvider, config, logger);

        await service.StartAsync(CancellationToken.None);
        await logger.WaitForMessagesAsync(1, Timeout);

        config.MailEnabled = true;
        timeProvider.Advance(HeartbeatService.Interval);
        await logger.WaitForMessagesAsync(2, Timeout);

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(
            [
                "Heartbeat role=crawler instance=instance-1 mail.enabled=False",
                "Heartbeat role=crawler instance=instance-1 mail.enabled=True",
            ],
            logger.Messages);
    }

    private sealed class StubConfig : IAppConfig
    {
        public bool MailEnabled { get; set; }

        public T Get<T>(string key, string scope = SettingDefinition.GlobalScope) =>
            key == SettingsCatalog.MailEnabled ? (T)(object)MailEnabled : throw new ArgumentException(key);

        public Task SetAsync(
            string key, string scope, JsonElement? value, Actor actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
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
