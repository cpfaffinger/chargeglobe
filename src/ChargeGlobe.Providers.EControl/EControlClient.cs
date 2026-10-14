using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChargeGlobe.Domain;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Providers.EControl;

/// <summary>Response of one search: the stations and the sanitized raw records (for <c>raw_snapshot</c>).</summary>
public sealed record SearchResult(IReadOnlyList<(StationDto Station, JsonElement Raw)> Stations);

/// <summary>One page of the register and the number of stations in it overall.</summary>
public sealed record StationsPage(int TotalResults, IReadOnlyList<(StationDto Station, JsonElement Raw)> Stations);

/// <summary>
/// HTTP client of the E-Control API (<c>https://api.e-control.at/charge/1.0/</c>). Key and referer are
/// read from the config service on every request; the rate limit lives in <see cref="RateLimitingHandler"/>.
/// An error status becomes a <see cref="ProviderHttpException"/> with the <c>Retry-After</c> of the source.
/// </summary>
public sealed class EControlClient(HttpClient http, IProviderSettings settings, TimeProvider? timeProvider = null)
{
    /// <summary>The largest page <c>GET /search/stations</c> returns.</summary>
    public const int MaxPageSize = 1000;

    public static readonly Uri BaseAddress = new("https://api.e-control.at/charge/1.0/");

    /// <summary>Fields with personal data of the operator's contact person; never stored (docs/08, GDPR).</summary>
    private static readonly string[] PersonalFields =
        ["contactName", "phoneCountryCode", "regionCode", "phoneNumber", "extensionNumber", "email", "owner"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary><c>GET /search</c>: the about 100 stations closest to the point, with points, status and prices.</summary>
    public async Task<SearchResult> SearchAsync(GeoPoint point, CancellationToken cancellationToken)
    {
        var uri = string.Create(
            CultureInfo.InvariantCulture, $"search?latitude={point.Latitude:0.######}&longitude={point.Longitude:0.######}");
        var stations = await GetAsync(uri, "search", cancellationToken) as JsonArray
            ?? throw new JsonException("E-Control search returned no array.");
        return new SearchResult(stations.OfType<JsonObject>().Select(Read<StationDto>).ToList());
    }

    /// <summary>
    /// <c>GET /search/stations</c>: one page of the whole register, ordered by the source; the source caps
    /// <paramref name="maxResults"/> at <see cref="MaxPageSize"/>. Stations come in the common form without live status.
    /// </summary>
    public async Task<StationsPage> SearchStationsAsync(int fromIndex, int maxResults, CancellationToken cancellationToken)
    {
        var uri = string.Create(CultureInfo.InvariantCulture, $"search/stations?fromIndex={fromIndex}&maxResults={maxResults}");
        var page = await GetAsync(uri, "search/stations", cancellationToken) as JsonObject
            ?? throw new JsonException("E-Control search/stations returned no object.");
        var stations = (page["stations"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(Read<CatalogStationDto>)
            .Select(item => (item.Station.ToStation(), item.Raw))
            .ToList();
        return new StationsPage(page["totalResults"]?.GetValue<int>() ?? 0, stations);
    }

    private async Task<JsonNode> GetAsync(string uri, string name, CancellationToken cancellationToken)
    {
        var apiKey = settings.ApiKey(EControlProvider.Key)
            ?? throw new ProviderNotConfiguredException(
                $"Setting provider.api_key for scope provider:{EControlProvider.Key} is not set.");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("apiKey", apiKey);
        request.Headers.Referrer = RefererUri(settings.Referer(EControlProvider.Key));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // The body of a 401 is a German explanation of the registration; the status says enough.
            throw new ProviderHttpException(
                $"E-Control {name} returned {(int)response.StatusCode}.", response.StatusCode, RetryAfter(response));
        }

        return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))
            ?? throw new JsonException($"E-Control {name} returned null.");
    }

    /// <summary>The announced wait of a <c>Retry-After</c> header (seconds or date), never negative.</summary>
    private TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - (timeProvider ?? TimeProvider.System).GetUtcNow() is var wait && wait > TimeSpan.Zero ? wait : TimeSpan.Zero,
            _ => null,
        };

    /// <summary>A bare host name (<c>example.org</c>) becomes <c>https://example.org/</c>.</summary>
    public static Uri? RefererUri(string? referer) =>
        string.IsNullOrWhiteSpace(referer)
            ? null
            : Uri.TryCreate(referer.Trim(), UriKind.Absolute, out var absolute) && absolute.Scheme is "https" or "http"
                ? absolute
                : new Uri($"https://{referer.Trim().Trim('/')}/");

    private static (T Station, JsonElement Raw) Read<T>(JsonObject station)
    {
        foreach (var field in PersonalFields)
        {
            station.Remove(field);
        }

        var raw = JsonSerializer.SerializeToElement(station);
        return (raw.Deserialize<T>(Json)!, raw);
    }
}
