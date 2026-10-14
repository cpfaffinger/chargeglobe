using System.Collections.Concurrent;
using System.Net;
using System.Text;
using ChargeGlobe.Providers.Abstractions;
using ChargeGlobe.Providers.EControl;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace ChargeGlobe.Providers.Tests;

/// <summary>Answers every request with the result of <see cref="Respond"/> and records the requests.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = respond;

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(Respond(request));
    }
}

internal sealed class StubSettings(string? apiKey = "test-key", string? referer = "example.org", bool enabled = true) : IProviderSettings
{
    public bool IsEnabled(string providerKey) => enabled;

    public string? ApiKey(string providerKey) => apiKey;

    public string? Referer(string providerKey) => referer;
}

internal sealed class ListLogger<T> : ILogger<T>
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Messages.Enqueue(formatter(state, exception));
}

internal static class EControl
{
    public static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    public static ProviderDescriptor EControlDescriptor => EControlProvider.DescriptorValue;

    public static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "econtrol", name));

    public static EControlClient Client(StubHandler handler, IProviderSettings? settings = null) =>
        new(new HttpClient(handler) { BaseAddress = EControlClient.BaseAddress }, settings ?? new StubSettings());

    public static (EControlProvider Provider, ListLogger<EControlProvider> Logger) Provider(StubHandler handler, IProviderSettings? settings = null)
    {
        var logger = new ListLogger<EControlProvider>();
        return (new EControlProvider(Client(handler, settings), EControlMapper.CreateDefault(), new FakeTimeProvider(Now), logger), logger);
    }

    /// <summary>A minimal station record as JSON, <paramref name="distance"/> in km.</summary>
    public static string Station(string id, double lat, double lon, double distance) =>
        $$"""{"countryId":"AT","operatorId":"TST","stationId":"{{id}}","location":{"lat":{{lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"lon":{{lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}}},"distance":{{distance.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"points":[]}""";
}
