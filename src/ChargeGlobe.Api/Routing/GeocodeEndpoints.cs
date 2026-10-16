using System.Globalization;
using ChargeGlobe.Api.Auth;
using ChargeGlobe.Api.OpenApi;
using ChargeGlobe.Application.Addresses;
using ChargeGlobe.Application.Locations;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Identity;

namespace ChargeGlobe.Api.Routing;

/// <param name="Returned">Number of hits in <c>data</c>.</param>
public sealed record GeocodeMeta(int Returned, IReadOnlyList<SourceAttribution> Attribution);

public sealed record GeocodeResponse(IReadOnlyList<AddressHit> Data, GeocodeMeta Meta);

/// <summary>Address search for start and destination of the route planner (docs/06, "Adresssuche").</summary>
public static class GeocodeEndpoints
{
    public const int Limit = 8;
    private const int MinQueryLength = 3;
    private const int MaxQueryLength = 100;

    public static readonly SourceAttribution ExampleAttribution = new(
        AddressImporter.SourceKey, "© Österreichisches Adressregister, Stichtagsdaten vom 01.10.2026", "CC-BY-4.0");

    public static IEndpointRouteBuilder MapGeocodeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/geocode", GeocodeAsync)
            .WithTags("Routing")
            .RequirePermission(Permissions.RouteRead)
            .WithName("geocodeAddress")
            .WithSummary("Find Austrian addresses by street, house number, postal code and place")
            .WithDescription(
                "Searches the addresses of the Austrian address register (BEV, every address with a position) for `q` " +
                "(3–100 characters, e.g. `Stephansplatz 1 Wien`, `Hauptplatz 1 4020`): every word of `q` must occur in " +
                "street, house number, postal code, locality or municipality (case-insensitive, `ß` = `ss`, `Str.` matches " +
                "`Straße`); at least one word needs three characters. Order: an exact house number first, then – with " +
                "`near` (`latitude,longitude`, WGS 84, e.g. the map center) – the nearest, then the most similar. At most " +
                $"{Limit} hits, each with `label` (\"Stephansplatz 1, 1010 Wien\") and the position to use as a waypoint of " +
                "`calculateRoute`. `meta.attribution` names the register and its reference date (CC BY 4.0). The query is " +
                "neither stored nor logged. 422 for a missing or too short `q` or an invalid `near`.")
            .Produces<GeocodeResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(
                StatusCodes.Status200OK,
                new GeocodeResponse(
                    [new AddressHit("Stephansplatz 1, 1010 Wien", 48.208_49, 16.372_69)],
                    new GeocodeMeta(1, [ExampleAttribution])));
        return endpoints;
    }

    private static async Task<IResult> GeocodeAsync(string? q, string? near, IAddressStore addresses, CancellationToken cancellationToken)
    {
        var text = q?.Trim() ?? string.Empty;
        var point = ParsePoint(near);
        var invalid = new Validation()
            .Require(text.Length is >= MinQueryLength and <= MaxQueryLength, "q", $"Must have {MinQueryLength} to {MaxQueryLength} characters.")
            .Require(near is null || point is not null, "near", "Must be latitude,longitude in WGS 84, e.g. 48.2085,16.3731.")
            .ToResult();
        if (invalid is not null)
        {
            return invalid;
        }

        var result = await addresses.SearchAsync(text, point, Limit, cancellationToken);
        return Results.Ok(new GeocodeResponse(result.Hits, new GeocodeMeta(result.Hits.Count, result.Attribution)));
    }

    /// <summary><c>latitude,longitude</c> within the valid ranges, else null.</summary>
    public static GeoPoint? ParsePoint(string? value)
    {
        var parts = value?.Split(',');
        return parts is { Length: 2 }
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude)
            && latitude is >= -90 and <= 90
            && longitude is >= -180 and <= 180
                ? new GeoPoint(latitude, longitude)
                : null;
    }
}
