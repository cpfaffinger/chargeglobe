using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace ChargeGlobe.Domain;

/// <summary>
/// Business identity of a charge point (ISO 15118 EVSE-ID, e.g. <c>AT*EVN*E1234*5</c>).
/// Equality is based on the normalized form without separators.
/// </summary>
public sealed partial record EvseId
{
    private EvseId(string normalized, string display)
    {
        Normalized = normalized;
        Display = display;
    }

    /// <summary>Upper-case form without separators, e.g. <c>ATEVNE12345</c>.</summary>
    public string Normalized { get; }

    /// <summary>Upper-case form with <c>*</c> as separator, as delivered, e.g. <c>AT*EVN*E1234*5</c>.</summary>
    public string Display { get; }

    public string CountryCode => Normalized[..2];

    public string OperatorId => Normalized[2..5];

    /// <summary>Everything after the leading <c>E</c> of the outlet part.</summary>
    public string OutletId => Normalized[6..];

    /// <summary>Longest last part that still counts as the outlet of a station ("1", "01", "DC1").</summary>
    public const int MaxOutletLength = 3;

    /// <summary>
    /// The charging station the EVSE-ID names when it has the four-part form <c>country*operator*E&lt;station&gt;*&lt;outlet&gt;</c>
    /// with a short outlet, e.g. <c>AT*MER*E006566</c> of <c>AT*MER*E006566*01</c>; null for other forms (<c>AT*GBC*E1</c>,
    /// without separators, or a long last part such as <c>AT*EST*E000*01994</c>, where the number of the point comes last).
    /// </summary>
    public string? StationPart
    {
        get
        {
            var parts = Display.Split('*');
            return parts.Length == 4 && parts.All(part => part.Length > 0) && parts[3].Length <= MaxOutletLength
                ? string.Join('*', parts[..3])
                : null;
        }
    }

    public static EvseId Parse(string? input) =>
        TryParse(input, out var evseId)
            ? evseId
            : throw new FormatException($"'{input}' is not a valid EVSE-ID.");

    public static bool TryParse(string? input, [NotNullWhen(true)] out EvseId? evseId)
    {
        evseId = null;
        if (input is null)
        {
            return false;
        }

        var candidate = input.Trim().ToUpperInvariant().Replace('-', '*').Replace(' ', '*');
        if (!SeparatedFormat().IsMatch(candidate))
        {
            return false;
        }

        var normalized = candidate.Replace("*", string.Empty);
        if (normalized.Length < 7)
        {
            // Outlet part consisted of separators only.
            return false;
        }

        evseId = new EvseId(normalized, candidate);
        return true;
    }

    public bool Equals(EvseId? other) => other is not null && Normalized == other.Normalized;

    public override int GetHashCode() => Normalized.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Normalized;

    [GeneratedRegex(@"^[A-Z]{2}\*?[A-Z0-9]{3}\*?E[A-Z0-9\*]{1,30}$")]
    private static partial Regex SeparatedFormat();
}
