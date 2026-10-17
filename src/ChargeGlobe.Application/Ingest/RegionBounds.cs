using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Application.Ingest;

/// <summary>
/// Bounding boxes of the states (ISO 3166-2) of the countries chargeglobe covers: the envelopes of the seeded region
/// polygons (<c>Persistence/Seed/regions-at.geojson</c>, <c>regions-de.geojson</c>, OpenStreetMap) rounded outwards
/// to 0.01°; an integration test checks that every box covers its polygon. The crawler covers a region by its box,
/// because the sources are searched by rectangles and the crawler must not depend on the seed of the API; overlaps
/// (Vienna lies inside the box of Lower Austria) only cost requests, the merge is idempotent, and the region of a
/// location comes from the polygons (trigger <c>location_assign_region</c>).
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

    public static IReadOnlyDictionary<string, BoundingBox> Germany { get; } = new Dictionary<string, BoundingBox>
    {
        ["DE-BB"] = new(11.26, 51.35, 14.77, 53.56), // Brandenburg
        ["DE-BE"] = new(13.08, 52.33, 13.77, 52.68), // Berlin
        ["DE-BW"] = new(7.51, 47.53, 10.50, 49.80), // Baden-Württemberg
        ["DE-BY"] = new(8.97, 47.27, 13.84, 50.57), // Bayern
        ["DE-HB"] = new(8.48, 53.01, 9.00, 53.62), // Bremen
        ["DE-HE"] = new(7.77, 49.39, 10.24, 51.66), // Hessen
        ["DE-HH"] = new(8.10, 53.39, 10.33, 54.03), // Hamburg
        ["DE-MV"] = new(10.59, 53.11, 14.42, 54.89), // Mecklenburg-Vorpommern
        ["DE-NI"] = new(6.34, 51.29, 11.60, 54.14), // Niedersachsen
        ["DE-NW"] = new(5.86, 50.32, 9.47, 52.54), // Nordrhein-Westfalen
        ["DE-RP"] = new(6.11, 48.96, 8.51, 50.95), // Rheinland-Pfalz
        ["DE-SH"] = new(7.52, 53.35, 11.68, 55.10), // Schleswig-Holstein
        ["DE-SL"] = new(6.35, 49.11, 7.41, 49.64), // Saarland
        ["DE-SN"] = new(11.87, 50.17, 15.05, 51.69), // Sachsen
        ["DE-ST"] = new(10.56, 50.93, 13.19, 53.05), // Sachsen-Anhalt
        ["DE-TH"] = new(9.87, 50.20, 12.66, 51.65), // Thüringen
    };

    /// <summary>All known regions, by code.</summary>
    public static IReadOnlyDictionary<string, BoundingBox> All { get; } =
        Austria.Concat(Germany).ToDictionary(entry => entry.Key, entry => entry.Value);

    /// <summary>The country of a region code (<c>AT-9</c> → <c>AT</c>).</summary>
    public static string Country(string code) => code[..2];

    /// <summary>The regions of the given countries, in code order.</summary>
    public static IReadOnlyList<(string Code, BoundingBox Bbox)> ForCountries(IEnumerable<string> countries)
    {
        var wanted = countries.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. All.Where(entry => wanted.Contains(Country(entry.Key))).OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => (entry.Key, entry.Value))];
    }

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
            [.. requested.Where(All.ContainsKey).Select(code => (code, All[code]))],
            [.. requested.Where(code => !All.ContainsKey(code))]);
    }
}
