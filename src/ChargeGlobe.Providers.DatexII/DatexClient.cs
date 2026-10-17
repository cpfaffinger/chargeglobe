using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Providers.DatexII;

/// <summary>
/// HTTP client of one DATEX II source: reads a page of a publication. Paging (<c>offset</c>/<c>limit</c>) and the
/// key header come from the <see cref="DatexProfile"/>; the key itself is read from the config service on every
/// request. The rate limit lives in <see cref="RateLimitingHandler"/>. An error status becomes a
/// <see cref="ProviderHttpException"/> with the <c>Retry-After</c> of the source, a body that is no JSON object a
/// <see cref="JsonException"/>.
/// </summary>
public sealed class DatexClient(HttpClient http, DatexProfile profile, IProviderSettings settings, TimeProvider? timeProvider = null)
{
    /// <summary>The document at <paramref name="publication"/>; with paging the <paramref name="limit"/> sites from <paramref name="offset"/>.</summary>
    public async Task<JsonNode> GetAsync(Uri publication, int offset, int limit, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, PageUri(publication, offset, limit));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (profile.ApiKeyHeader is { } header)
        {
            request.Headers.Add(header, settings.ApiKey(profile.Key)
                ?? throw new ProviderNotConfiguredException($"Setting provider.api_key for scope provider:{profile.Key} is not set."));
            if (settings.Referer(profile.Key) is { } referer)
            {
                request.Headers.Referrer = Uri.TryCreate(referer, UriKind.Absolute, out var absolute) && absolute.Scheme is "https" or "http"
                    ? absolute
                    : new Uri($"https://{referer.Trim('/')}/");
            }
        }

        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ProviderHttpException(
                $"DATEX II {profile.Key} returned {(int)response.StatusCode}.", response.StatusCode, RetryAfter(response));
        }

        return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) as JsonObject
            ?? throw new JsonException($"DATEX II {profile.Key} returned no JSON object.");
    }

    /// <summary>The publication with <c>offset</c> and <c>limit</c> added to its query, or as is without paging.</summary>
    public Uri PageUri(Uri publication, int offset, int limit)
    {
        if (profile.PageSize == 0)
        {
            return publication;
        }

        var builder = new UriBuilder(publication);
        var paging = string.Create(CultureInfo.InvariantCulture, $"offset={offset}&limit={limit}");
        builder.Query = builder.Query.Length > 1 ? $"{builder.Query[1..]}&{paging}" : paging;
        return builder.Uri;
    }

    /// <summary>The announced wait of a <c>Retry-After</c> header (seconds or date), never negative.</summary>
    private TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - (timeProvider ?? TimeProvider.System).GetUtcNow() is var wait && wait > TimeSpan.Zero ? wait : TimeSpan.Zero,
            _ => null,
        };
}
