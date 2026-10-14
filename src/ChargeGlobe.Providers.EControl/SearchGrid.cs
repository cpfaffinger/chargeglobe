using System.Runtime.CompilerServices;
using System.Text.Json;
using ChargeGlobe.Domain;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Providers.EControl;

/// <summary>
/// Covers a bounding box with <c>GET /search</c>, which only returns the <see cref="PageSize"/> stations
/// closest to a point (docs/08, polling budget). Each cell is searched from its center; if the result
/// is a full page whose farthest station is closer than the corners of the cell, the cell is split
/// into four. Dense areas thus get fine cells and rural areas coarse ones. One instance per run.
/// </summary>
public sealed class SearchGrid(Func<GeoPoint, CancellationToken, Task<SearchResult>> search, int maxRequests)
{
    public const int PageSize = 100;

    /// <summary>Cells are not split below this half diagonal (stations stacked on one spot).</summary>
    public const double MinHalfDiagonalKm = 0.05;

    private const double EarthRadiusKm = 6371.0088;

    public int Requests { get; private set; }

    /// <summary>The budget ended the run before the box was covered.</summary>
    public bool Exhausted { get; private set; }

    /// <summary>Every station in the box once; stations without ID or coordinates are passed on for the mapper to report.</summary>
    public async IAsyncEnumerable<(StationDto Station, JsonElement Raw)> CoverAsync(
        BoundingBox box, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var pending = new Queue<BoundingBox>([box]);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryDequeue(out var cell))
        {
            if (Requests == maxRequests)
            {
                Exhausted = true;
                yield break;
            }

            var center = new GeoPoint((cell.MinLatitude + cell.MaxLatitude) / 2, (cell.MinLongitude + cell.MaxLongitude) / 2);
            var result = await search(center, cancellationToken);
            Requests++;

            foreach (var (station, raw) in result.Stations)
            {
                var id = EControlMapper.StationExternalId(station);
                var inside = station.Location is not { Lat: { } lat, Lon: { } lon } || Contains(box, lat, lon);
                if (inside && (id is null || seen.Add(id)))
                {
                    yield return (station, raw);
                }
            }

            var halfDiagonal = DistanceKm(center, new GeoPoint(cell.MaxLatitude, cell.MaxLongitude));
            var reach = result.Stations.Count < PageSize ? double.PositiveInfinity : result.Stations.Max(item => item.Station.Distance ?? 0);
            if (reach < halfDiagonal && halfDiagonal > MinHalfDiagonalKm)
            {
                foreach (var quarter in Split(cell, center))
                {
                    pending.Enqueue(quarter);
                }
            }
        }
    }

    /// <summary>Great-circle distance (haversine).</summary>
    public static double DistanceKm(GeoPoint from, GeoPoint to)
    {
        static double Radians(double degrees) => degrees * Math.PI / 180;
        var dLat = Radians(to.Latitude - from.Latitude);
        var dLon = Radians(to.Longitude - from.Longitude);
        var a = Math.Pow(Math.Sin(dLat / 2), 2)
            + (Math.Cos(Radians(from.Latitude)) * Math.Cos(Radians(to.Latitude)) * Math.Pow(Math.Sin(dLon / 2), 2));
        return 2 * EarthRadiusKm * Math.Asin(Math.Sqrt(a));
    }

    private static bool Contains(BoundingBox box, double latitude, double longitude) =>
        latitude >= box.MinLatitude && latitude <= box.MaxLatitude && longitude >= box.MinLongitude && longitude <= box.MaxLongitude;

    private static IEnumerable<BoundingBox> Split(BoundingBox cell, GeoPoint center) =>
    [
        new(cell.MinLongitude, cell.MinLatitude, center.Longitude, center.Latitude),
        new(center.Longitude, cell.MinLatitude, cell.MaxLongitude, center.Latitude),
        new(cell.MinLongitude, center.Latitude, center.Longitude, cell.MaxLatitude),
        new(center.Longitude, center.Latitude, cell.MaxLongitude, cell.MaxLatitude),
    ];
}
