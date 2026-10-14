namespace ChargeGlobe.Domain;

/// <summary>A position in WGS 84 (SRID 4326), stored as PostGIS <c>geography(Point, 4326)</c>.</summary>
public readonly record struct GeoPoint
{
    public GeoPoint(double latitude, double longitude)
    {
        if (!(latitude is >= -90 and <= 90))
        {
            throw new ArgumentOutOfRangeException(nameof(latitude), latitude, "Latitude must be within -90 and 90.");
        }

        if (!(longitude is >= -180 and <= 180))
        {
            throw new ArgumentOutOfRangeException(nameof(longitude), longitude, "Longitude must be within -180 and 180.");
        }

        Latitude = latitude;
        Longitude = longitude;
    }

    public double Latitude { get; }

    public double Longitude { get; }
}
