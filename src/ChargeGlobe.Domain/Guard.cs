using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace ChargeGlobe.Domain;

/// <summary>Argument checks shared by the entities.</summary>
internal static partial class Guard
{
    public static string NotBlank(string? value, [CallerArgumentExpression(nameof(value))] string? name = null) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Must not be empty.", name) : value.Trim();

    /// <summary>ISO 3166-1 alpha-2, upper case (e.g. <c>AT</c>).</summary>
    public static string CountryCode(string? value, [CallerArgumentExpression(nameof(value))] string? name = null) =>
        value is not null && CountryPattern().IsMatch(value)
            ? value
            : throw new ArgumentException($"'{value}' is not an ISO 3166-1 alpha-2 code in upper case.", name);

    public static double Fraction(double value, [CallerArgumentExpression(nameof(value))] string? name = null) =>
        value is >= 0 and <= 1 ? value : throw new ArgumentOutOfRangeException(name, value, "Must be within 0 and 1.");

    [GeneratedRegex("^[A-Z]{2}$")]
    private static partial Regex CountryPattern();
}
