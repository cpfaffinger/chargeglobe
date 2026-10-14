using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Application.Ingest;

/// <summary>
/// Bounding boxes of the Austrian states (ISO 3166-2): the envelopes of the seeded region polygons
/// (<c>Persistence/Seed/regions-at.geojson</c>, OpenStreetMap) rounded outwards to 0.01°; an integration test
/// checks that every box covers its polygon. The crawler covers a region by its box, because the sources are
/// searched by rectangles and the crawler must not depend on the seed of the API; overlaps (Vienna lies inside
/// the box of Lower Austria) only cost requests, the merge is idempotent, and the region of a location comes from
/// the polygons (trigger <c>location_assign_region</c>).
/// </summary>
public static class RegionBounds
{
    public static IReadOnlyDictionary<string, BoundingBox> Austria { get; } = new Dictionary<string, BoundingBox>
    {
        ["AT-1"] = new(15.99, 46.83, 17.17, 48.12), // Burgenland
        ["AT-2"] = new(12.65, 46.37, 15.07, 47.14), // Kärnten
        ["AT-3"] = new(14.45, 47.42, 17.07, 49.03), // Niederösterreich
        ["AT-4"] = new(12.74, 47.46, 15.00, 48.78), // Oberösterreich
        ["AT-5"] = new(12.07, 46.94, 14.00, 48.05), // Salzburg
        ["AT-6"] = new(13.56, 46.61, 16.18, 47.83), // Steiermark
        ["AT-7"] = new(10.09, 46.65, 12.97, 47.75), // Tirol
        ["AT-8"] = new(9.53, 46.84, 10.24, 47.60), // Vorarlberg
        ["AT-9"] = new(16.18, 48.11, 16.58, 48.33), // Wien
    };

    /// <summary>
    /// The known regions of a comma-separated list (setting <c>provider.regions</c>), in the given order
    /// and without duplicates; unknown codes are returned separately.
    /// </summary>
    public static (IReadOnlyList<(string Code, BoundingBox Bbox)> Known, IReadOnlyList<string> Unknown) Parse(string? codes)
    {
        var requested = (codes ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(code => code.ToUpperInvariant())
            .Distinct()
            .ToList();
        return (
            [.. requested.Where(Austria.ContainsKey).Select(code => (code, Austria[code]))],
            [.. requested.Where(code => !Austria.ContainsKey(code))]);
    }
}
