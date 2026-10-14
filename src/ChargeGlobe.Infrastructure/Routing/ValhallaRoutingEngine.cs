using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ChargeGlobe.Application.Routing;
using ChargeGlobe.Domain;
using Microsoft.Extensions.Logging;

namespace ChargeGlobe.Infrastructure.Routing;

/// <summary>
/// Adapter to Valhalla (<c>POST /route</c> of the compose service <c>routing</c>, docs/adr/0007): costing <c>auto</c>,
/// lengths in kilometres, no turn-by-turn directions. The line comes as polyline with six decimals per leg. Logs only
/// the error of the engine, never the positions.
/// </summary>
public sealed partial class ValhallaRoutingEngine(HttpClient http, ILogger<ValhallaRoutingEngine> logger) : IRoutingEngine
{
    public async Task<RouteResult> RouteAsync(IReadOnlyList<GeoPoint> waypoints, CancellationToken cancellationToken)
    {
        var request = new ValhallaRequest(
            [.. waypoints.Select(point => new ValhallaLocation(point.Latitude, point.Longitude))], "auto", "kilometers", "none");
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync("route", request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogUnavailable(logger, exception.Message);
            return new RouteResult(RouteOutcome.Unavailable, null);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                // Valhalla answers 400 when it finds no path (442) or no road near a point (171).
                var error = await response.Content.ReadFromJsonAsync<ValhallaError>(cancellationToken);
                LogNoRoute(logger, error?.ErrorCode, error?.Error);
                return new RouteResult(RouteOutcome.NoRoute, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                LogUnavailable(logger, $"HTTP {(int)response.StatusCode}");
                return new RouteResult(RouteOutcome.Unavailable, null);
            }

            var trip = (await response.Content.ReadFromJsonAsync<ValhallaResponse>(cancellationToken))!.Trip;
            // Each leg starts where the previous one ends; the joint is kept once.
            var geometry = trip.Legs
                .SelectMany((leg, index) => Polyline.Decode(leg.Shape).Skip(index == 0 ? 0 : 1))
                .ToList();
            return new RouteResult(
                RouteOutcome.Found, new Route(trip.Summary.Length * 1000, trip.Summary.Time, geometry));
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Routing engine unavailable: {Reason}")]
    private static partial void LogUnavailable(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Routing engine found no route: {Code} {Error}")]
    private static partial void LogNoRoute(ILogger logger, int? code, string? error);

    private sealed record ValhallaRequest(
        IReadOnlyList<ValhallaLocation> Locations,
        string Costing,
        string Units,
        [property: JsonPropertyName("directions_type")] string DirectionsType);

    private sealed record ValhallaLocation(double Lat, double Lon);

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
