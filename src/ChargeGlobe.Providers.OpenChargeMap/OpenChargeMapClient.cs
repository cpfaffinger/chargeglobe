using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Providers.OpenChargeMap;

/// <summary>
/// HTTP client of the Open Charge Map API (<c>https://api.openchargemap.io/v3/</c>). The key is read from the config
/// service on every request and sent as <c>X-API-Key</c>; the rate limit lives in <see cref="RateLimitingHandler"/>. An
/// error status becomes a <see cref="ProviderHttpException"/> with the <c>Retry-After</c> of the source.
/// </summary>
public sealed class OpenChargeMapClient(HttpClient http, IProviderSettings settings, TimeProvider? timeProvider = null)
{
    /// <summary>Sites per request; the adapter pages with <c>greaterthanid</c>.</summary>
    public const int PageSize = 1000;

    public static readonly Uri BaseAddress = new("https://api.openchargemap.io/v3/");

    /// <summary>Contact data of operators and sites and the user content (names in comments and photos); never stored.</summary>
    private static readonly string[] PersonalFields = ["UserComments", "MediaItems"];

    private static readonly string[] PersonalAddressFields = ["ContactTelephone1", "ContactTelephone2", "ContactEmail"];

    private static readonly string[] PersonalOperatorFields =
        ["PhonePrimaryContact", "PhoneSecondaryContact", "ContactEmail", "FaultReportEmail", "AddressInfo"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// <c>GET /poi</c>: up to <paramref name="maxResults"/> open-data sites of the country inside the box with an ID above
    /// <paramref name="afterId"/>, by ID. Reference objects (operator, usage) are included, empty values left out.
    /// </summary>
    public async Task<IReadOnlyList<(PoiDto Poi, JsonElement Raw)>> PoisAsync(
        BoundingBox box, string country, int afterId, int maxResults, CancellationToken cancellationToken)
    {
        // Top left and bottom right corner: (north,west),(south,east).
        var uri = string.Create(
            CultureInfo.InvariantCulture,
            $"poi?output=json&client=chargeglobe&countrycode={country}&opendata=true&compact=false&verbose=false&includecomments=false"
            + $"&boundingbox=({box.MaxLatitude:0.######},{box.MinLongitude:0.######}),({box.MinLatitude:0.######},{box.MaxLongitude:0.######})"
            + $"&sortby=id_asc&greaterthanid={afterId}&maxresults={maxResults}");
        var apiKey = settings.ApiKey(OpenChargeMapProvider.Key)
            ?? throw new ProviderNotConfiguredException(
                $"Setting provider.api_key for scope provider:{OpenChargeMapProvider.Key} is not set.");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("X-API-Key", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ProviderHttpException(
                $"Open Charge Map poi returned {(int)response.StatusCode}.", response.StatusCode, RetryAfter(response));
        }

        var sites = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) as JsonArray
            ?? throw new JsonException("Open Charge Map poi returned no array.");
        return [.. sites.OfType<JsonObject>().Select(Read)];
    }

    /// <summary>The announced wait of a <c>Retry-After</c> header (seconds or date), never negative.</summary>
    private TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - (timeProvider ?? TimeProvider.System).GetUtcNow() is var wait && wait > TimeSpan.Zero ? wait : TimeSpan.Zero,
            _ => null,
        };

    private static (PoiDto Poi, JsonElement Raw) Read(JsonObject site)
    {
        Remove(site, PersonalFields);
        Remove(site["AddressInfo"] as JsonObject, PersonalAddressFields);
        Remove(site["OperatorInfo"] as JsonObject, PersonalOperatorFields);
        var raw = JsonSerializer.SerializeToElement(site);
        return (raw.Deserialize<PoiDto>(Json)!, raw);
    }

    private static void Remove(JsonObject? node, string[] fields)
    {
        foreach (var field in fields)
        {
            node?.Remove(field);
        }
    }
}
