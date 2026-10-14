using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChargeGlobe.Domain;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Providers.EControl;

/// <summary>Response of one search: the stations and the sanitized raw records (for <c>raw_snapshot</c>).</summary>
public sealed record SearchResult(IReadOnlyList<(StationDto Station, JsonElement Raw)> Stations);

/// <summary>
/// HTTP client of the E-Control API (<c>https://api.e-control.at/charge/1.0/</c>). Key and referer are
/// read from the config service on every request; rate limit and Retry-After live in
/// <see cref="RateLimitingHandler"/>.
/// </summary>
public sealed class EControlClient(HttpClient http, IProviderSettings settings)
{
    public static readonly Uri BaseAddress = new("https://api.e-control.at/charge/1.0/");

    /// <summary>Fields with personal data of the operator's contact person; never stored (docs/08, GDPR).</summary>
    private static readonly string[] PersonalFields =
        ["contactName", "phoneCountryCode", "regionCode", "phoneNumber", "extensionNumber", "email", "owner"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary><c>GET /search</c>: the about 100 stations closest to the point, with points, status and prices.</summary>
    public async Task<SearchResult> SearchAsync(GeoPoint point, CancellationToken cancellationToken)
    {
        var apiKey = settings.ApiKey(EControlProvider.Key)
            ?? throw new ProviderNotConfiguredException(
                $"Setting provider.api_key for scope provider:{EControlProvider.Key} is not set.");
        var uri = string.Create(
            CultureInfo.InvariantCulture, $"search?latitude={point.Latitude:0.######}&longitude={point.Longitude:0.######}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("apiKey", apiKey);
        request.Headers.Referrer = RefererUri(settings.Referer(EControlProvider.Key));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // The body of a 401 is a German explanation of the registration; the status says enough.
            throw new HttpRequestException(
                $"E-Control search returned {(int)response.StatusCode}.", null, response.StatusCode);
        }

        var stations = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))?.AsArray()
            ?? throw new JsonException("E-Control search returned null instead of an array.");
        return new SearchResult(stations.OfType<JsonObject>().Select(Read).ToList());
    }

    /// <summary>A bare host name (<c>example.org</c>) becomes <c>https://example.org/</c>.</summary>
    public static Uri? RefererUri(string? referer) =>
        string.IsNullOrWhiteSpace(referer)
            ? null
            : Uri.TryCreate(referer.Trim(), UriKind.Absolute, out var absolute) && absolute.Scheme is "https" or "http"
                ? absolute
                : new Uri($"https://{referer.Trim().Trim('/')}/");

    private static (StationDto, JsonElement) Read(JsonObject station)
    {
        foreach (var field in PersonalFields)
        {
            station.Remove(field);
        }

        var raw = JsonSerializer.SerializeToElement(station);
        return (raw.Deserialize<StationDto>(Json)!, raw);
    }
}
