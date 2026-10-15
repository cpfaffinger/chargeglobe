using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChargeGlobe.Application.Routing;
using ChargeGlobe.Domain;
using Microsoft.Extensions.Logging;

namespace ChargeGlobe.Infrastructure.Routing;

/// <summary>
/// Adapter to Valhalla (<c>POST /route</c> and <c>POST /isochrone</c> of the compose service <c>routing</c>,
/// docs/adr/0007): costing <c>auto</c>, lengths in kilometres, no turn-by-turn directions. The line comes as polyline with
/// six decimals per leg, the isochrones as GeoJSON polygons. Logs only the error of the engine, never the positions.
/// </summary>
public sealed class ValhallaRoutingEngine(HttpClient http, ILogger<ValhallaRoutingEngine> logger) : IRoutingEngine
{
    /// <summary>Address of the engine inside the compose network (service <c>routing</c>).</summary>
    public static readonly Uri Address = new("http://routing:8002/");

    /// <summary>A route through Austria takes well under a second; a hanging engine must not block the request.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>Smoothing of the isochrones: removes small islands (share of the largest) and simplifies to about this many metres.</summary>
    public const double Denoise = 0.5;
    public const double Generalize = 150;

    public async Task<RouteResult> RouteAsync(IReadOnlyList<GeoPoint> waypoints, CancellationToken cancellationToken)
    {
        var request = new ValhallaRequest(
            [.. waypoints.Select(point => new ValhallaLocation(point.Latitude, point.Longitude))], "auto", "kilometers", "none");
        var (outcome, response) = await SendAsync("route", request, cancellationToken);
        if (response is null)
        {
            return new RouteResult(outcome, null);
        }

        using (response)
        {
            var trip = (await response.Content.ReadFromJsonAsync<ValhallaResponse>(cancellationToken))!.Trip;
            // Each leg starts where the previous one ends; the joint is kept once.
            var geometry = trip.Legs
                .SelectMany((leg, index) => Polyline.Decode(leg.Shape).Skip(index == 0 ? 0 : 1))
                .ToList();
            return new RouteResult(
                RouteOutcome.Found, new Route(trip.Summary.Length * 1000, trip.Summary.Time, geometry));
        }
    }

    public async Task<ReachResult> ReachAsync(GeoPoint position, ReachMetric metric, IReadOnlyList<int> values, CancellationToken cancellationToken)
    {
        var request = new ValhallaIsochroneRequest(
            [new ValhallaLocation(position.Latitude, position.Longitude)],
            "auto",
            [.. values.Select(value => metric == ReachMetric.Minutes ? new ValhallaContour(value, null) : new ValhallaContour(null, value))],
            true,
            Denoise,
            Generalize);
        var (outcome, response) = await SendAsync("isochrone", request, cancellationToken);
        if (response is null)
        {
            return new ReachResult(outcome, null);
        }

        using (response)
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var areas = document.RootElement.GetProperty("features").EnumerateArray()
                .Select(feature =>
                {
                    var geometry = feature.GetProperty("geometry");
                    var coordinates = geometry.GetProperty("coordinates");
                    // A Polygon is one polygon of rings; a MultiPolygon a list of them.
                    var polygons = geometry.GetProperty("type").GetString() == "MultiPolygon"
                        ? coordinates.EnumerateArray().Select(Rings).ToList()
                        : [Rings(coordinates)];
                    return new ReachArea((int)Math.Round(feature.GetProperty("properties").GetProperty("contour").GetDouble()), polygons);
                })
                .OrderByDescending(area => area.Value)
                .ToList();
            return new ReachResult(RouteOutcome.Found, areas);
        }
    }

    private static IReadOnlyList<IReadOnlyList<GeoPoint>> Rings(JsonElement polygon) =>
        [.. polygon.EnumerateArray().Select(ring => (IReadOnlyList<GeoPoint>)[.. ring.EnumerateArray().Select(point => new GeoPoint(point[1].GetDouble(), point[0].GetDouble()))])];

    /// <summary>
    /// Posts to the engine; the response when it succeeded, otherwise null with <see cref="RouteOutcome.NoRoute"/> for
    /// 400 (no path, no road near a point, a limit of the engine) or <see cref="RouteOutcome.Unavailable"/>.
    /// </summary>
    private async Task<(RouteOutcome Outcome, HttpResponseMessage? Response)> SendAsync<T>(string path, T request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(path, request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning("Routing engine unavailable: {Reason}", exception.Message);
            return (RouteOutcome.Unavailable, null);
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            // Valhalla answers 400 when it finds no path (442), no road near a point (171) or a limit is exceeded.
            using (response)
            {
                var error = await response.Content.ReadFromJsonAsync<ValhallaError>(cancellationToken);
                logger.LogInformation("Routing engine found no result: {Code} {Error}", error?.ErrorCode, error?.Error);
                return (RouteOutcome.NoRoute, null);
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Routing engine unavailable: HTTP {Status}", (int)response.StatusCode);
            response.Dispose();
            return (RouteOutcome.Unavailable, null);
        }

        return (RouteOutcome.Found, response);
    }

    // Plain logger calls: coverlet cannot resolve the [LoggerMessage] attribute when it instruments this assembly.
    private sealed record ValhallaRequest(
        IReadOnlyList<ValhallaLocation> Locations,
        string Costing,
        string Units,
        [property: JsonPropertyName("directions_type")] string DirectionsType);

    private sealed record ValhallaLocation(double Lat, double Lon);

    private sealed record ValhallaIsochroneRequest(
        IReadOnlyList<ValhallaLocation> Locations,
        string Costing,
        IReadOnlyList<ValhallaContour> Contours,
        bool Polygons,
        double Denoise,
        double Generalize);

    /// <summary>One contour: <c>time</c> in minutes or <c>distance</c> in kilometres.</summary>
    private sealed record ValhallaContour(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Time,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Distance);

    private sealed record ValhallaError([property: JsonPropertyName("error_code")] int? ErrorCode, string? Error);

    private sealed record ValhallaResponse(ValhallaTrip Trip);

    private sealed record ValhallaTrip(ValhallaSummary Summary, IReadOnlyList<ValhallaLeg> Legs);

    private sealed record ValhallaSummary(double Length, double Time);

    private sealed record ValhallaLeg(string Shape);
}

/// <summary>Encoded polyline with six decimals (the format of Valhalla), see the Google polyline algorithm.</summary>
public static class Polyline
{
    private const double Factor = 1e6;

    public static IReadOnlyList<GeoPoint> Decode(string encoded)
    {
        var points = new List<GeoPoint>();
        var index = 0;
        long latitude = 0;
        long longitude = 0;
        while (index < encoded.Length)
        {
            latitude += Next(encoded, ref index);
            longitude += Next(encoded, ref index);
            points.Add(new GeoPoint(latitude / Factor, longitude / Factor));
        }

        return points;
    }

    private static long Next(string encoded, ref int index)
    {
        long result = 0;
        var shift = 0;
        int chunk;
        do
        {
            chunk = encoded[index++] - 63;
            result |= (long)(chunk & 0x1f) << shift;
            shift += 5;
        }
        while (chunk >= 0x20);

        return (result & 1) != 0 ? ~(result >> 1) : result >> 1;
    }
}
