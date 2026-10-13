using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace ChargeGlobe.Domain;

/// <summary>
/// Business identity of a charge point (ISO 15118 EVSE-ID, e.g. <c>AT*EVN*E1234*5</c>).
/// Equality is based on the normalized form without separators.
/// </summary>
public sealed partial record EvseId
{
    private EvseId(string normalized)
    {
        Normalized = normalized;
    }

    /// <summary>Upper-case form without separators, e.g. <c>ATEVNE12345</c>.</summary>
    public string Normalized { get; }

    public string CountryCode => Normalized[..2];

    public string OperatorId => Normalized[2..5];

    /// <summary>Everything after the leading <c>E</c> of the outlet part.</summary>
    public string OutletId => Normalized[6..];

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

        evseId = new EvseId(normalized);
        return true;
    }

    public override string ToString() => Normalized;

    [GeneratedRegex(@"^[A-Z]{2}\*?[A-Z0-9]{3}\*?E[A-Z0-9\*]{1,30}$")]
    private static partial Regex SeparatedFormat();
}
