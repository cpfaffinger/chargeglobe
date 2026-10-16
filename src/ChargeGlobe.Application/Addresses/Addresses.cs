using System.Text;
using ChargeGlobe.Application.Locations;
using ChargeGlobe.Domain;

namespace ChargeGlobe.Application.Addresses;

/// <summary>
/// An address of the register, already resolved: street, house number, postal code, locality (Ortschaft) and
/// municipality (Gemeinde) by name; the position in the projected coordinates of the source (<paramref name="Epsg"/>).
/// </summary>
/// <param name="Code">Address code of the register (ADRCD), unique.</param>
public sealed record AddressRecord(
    long Code,
    string Street,
    string HouseNumber,
    string PostalCode,
    string Locality,
    string Municipality,
    string MunicipalityCode,
    double Easting,
    double Northing,
    int Epsg)
{
    /// <summary>
    /// The place name of the label: the locality, unless it only names a part of its municipality ("Wien,Innere
    /// Stadt"), then the municipality.
    /// </summary>
    public string City => Locality.StartsWith(Municipality + ",", StringComparison.Ordinal) ? Municipality : Locality;

    /// <summary>What the search compares with (column <c>search_text</c>), normalized like a query.</summary>
    public string SearchText => AddressText.Normalize($"{Street} {HouseNumber} {PostalCode} {Locality} {Municipality}");
}

/// <summary>One normalization for stored addresses and queries, so that both meet.</summary>
public static class AddressText
{
    /// <summary>
    /// Lower case, "ß" as "ss" (people type "Strasse"), punctuation as space ("Str." matches "strasse", "Wien,Innere
    /// Stadt" two words), runs of spaces as one.
    /// </summary>
    public static string Normalize(string text)
    {
        var normalized = new StringBuilder(text.Length);
        foreach (var character in text.ToLowerInvariant())
        {
            if (character == 'ß')
            {
                normalized.Append("ss");
            }
            else if (char.IsLetterOrDigit(character) || character == '/')
            {
                normalized.Append(character);
            }
            else if (normalized.Length > 0 && normalized[^1] != ' ')
            {
                normalized.Append(' ');
            }
        }

        return normalized.ToString().TrimEnd();
    }

    /// <summary>The words of a normalized query.</summary>
    public static string[] Words(string query) => Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>A hit of the address search.</summary>
/// <param name="Label">E.g. "Hauptplatz 1, 4020 Linz".</param>
public sealed record AddressHit(string Label, double Latitude, double Longitude);

public sealed record AddressSearchResult(IReadOnlyList<AddressHit> Hits, IReadOnlyList<SourceAttribution> Attribution);

/// <summary>The imported reference date (Stichtag) and when it was imported and last checked for a newer one.</summary>
public sealed record AddressImportState(DateOnly ReferenceDate, DateTimeOffset ImportedAt, DateTimeOffset CheckedAt, int Addresses);

/// <summary>A published reference date of the register and where to download it.</summary>
public sealed record AddressRegisterRelease(DateOnly ReferenceDate, Uri Url);

/// <summary>An opened download of the register: its reference date and its addresses, read as they are needed.</summary>
public interface IAddressRegisterFile : IAsyncDisposable
{
    DateOnly ReferenceDate { get; }

    IAsyncEnumerable<AddressRecord> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>The address register of the BEV (docs/endpunkte/bev-adressen.md): finds and downloads releases.</summary>
public interface IAddressRegister
{
    /// <summary>The newest release that is online; null if none of the expected reference dates is.</summary>
    Task<AddressRegisterRelease?> FindNewestAsync(CancellationToken cancellationToken);

    /// <summary>Downloads the release to a temporary file and opens it; disposing deletes the file.</summary>
    Task<IAddressRegisterFile> OpenAsync(AddressRegisterRelease release, CancellationToken cancellationToken);
}

/// <summary>The addresses of the geocoder in the database (table <c>address</c>, docs/03).</summary>
public interface IAddressStore
{
    Task<AddressImportState?> StateAsync(CancellationToken cancellationToken);

    /// <summary>Notes that no newer release than the imported one exists.</summary>
    Task MarkCheckedAsync(DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces all addresses with <paramref name="records"/>: loaded into a staging table, indexed, and swapped in one
    /// transaction, so searches see the old or the new addresses but never a mix. Returns the number of addresses.
    /// </summary>
    Task<int> ReplaceAsync(IAsyncEnumerable<AddressRecord> records, DateOnly referenceDate, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>
    /// Addresses whose street, house number, postal code, locality and municipality contain every word of the query,
    /// the exact house number and the most similar first, then the nearest to <paramref name="near"/>.
    /// </summary>
    Task<AddressSearchResult> SearchAsync(string query, GeoPoint? near, int limit, CancellationToken cancellationToken);
}
