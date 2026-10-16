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

    /// <summary>
    /// What the search compares the street with (column <c>address_street.search_text</c>): street, postal code,
    /// locality and municipality, normalized like a query. The house number is matched separately.
    /// </summary>
    public string StreetSearchText => AddressText.Normalize($"{Street} {PostalCode} {Locality} {Municipality}");
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

/// <summary>
/// A query split for the two stages of the search: words the trigram index of the streets can look up, short words
/// that only filter the streets, and house numbers, which the street or one of its addresses must contain.
/// </summary>
/// <param name="Indexed">Words of three or more characters, except numbers shorter than a postal code.</param>
/// <param name="Short">Words of one or two characters that are no numbers ("am").</param>
/// <param name="Numbers">
/// Words that start with a digit and are shorter than a postal code ("1", "12a", "3/2"): a house number that starts
/// with the word, or a street (postal code) that contains it.
/// </param>
/// <param name="StreetText">The words of the street stage, for the similarity order.</param>
public sealed record AddressQuery(string[] Indexed, string[] Short, string[] Numbers, string StreetText)
{
    /// <summary>Shortest word with a trigram.</summary>
    public const int MinIndexedWord = 3;

    /// <summary>Austrian postal codes have four digits; shorter numbers are taken as house numbers.</summary>
    public const int PostalCodeLength = 4;

    /// <summary>Without an indexed word the streets cannot be looked up; such a query finds nothing.</summary>
    public bool Searchable => Indexed.Length > 0;

    public static AddressQuery Parse(string query)
    {
        var words = AddressText.Words(query);
        bool IsNumber(string word) => char.IsDigit(word[0]) && word.Length < PostalCodeLength;
        var indexed = words.Where(word => word.Length >= MinIndexedWord && !IsNumber(word)).ToArray();
        var short1 = words.Where(word => word.Length < MinIndexedWord && !IsNumber(word)).ToArray();
        return new AddressQuery(indexed, short1, [.. words.Where(IsNumber)], string.Join(' ', [.. indexed, .. short1]));
    }
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
    /// Addresses whose street, postal code, locality and municipality contain every word of the query, house numbers
    /// as the start of the house number: the exact house number first, then the nearest to <paramref name="near"/>,
    /// then the most similar street. Without a house number in the query one address per street, the lowest number.
    /// </summary>
    Task<AddressSearchResult> SearchAsync(string query, GeoPoint? near, int limit, CancellationToken cancellationToken);
}
