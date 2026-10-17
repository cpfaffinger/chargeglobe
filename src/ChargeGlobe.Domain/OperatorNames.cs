namespace ChargeGlobe.Domain;

/// <summary>Comparison of operator names across sources (docs/03, matching step 3); also used by adapters that group records of one operator (Bundesnetzagentur).</summary>
public static class OperatorNames
{
    /// <summary>Legal forms and fill words that say nothing about the operator.</summary>
    private static readonly HashSet<string> Noise =
        ["gmbh", "ag", "kg", "co", "mbh", "und", "eu", "se", "ltd", "inc", "e", "u", "gesmbh", "og"];

    /// <summary>
    /// Same operator if, after dropping legal forms and punctuation, the words of the shorter name all occur in the longer
    /// one (<c>Wien Energie</c> / <c>TANKE Wien Energie</c>) or both start with the same word (<c>Tesla</c> /
    /// <c>Tesla Germany GmbH</c>).
    /// </summary>
    public static bool Same(string? left, string? right)
    {
        var (a, b) = (Words(left), Words(right));
        if (a.Count == 0 || b.Count == 0)
        {
            return false;
        }

        var (shorter, longer) = a.Count <= b.Count ? (a, b) : (b, a);
        return shorter.All(longer.Contains) || a[0] == b[0];
    }

    private static List<string> Words(string? name) =>
        [.. new string([.. (name ?? string.Empty).ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : ' ')])
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !Noise.Contains(word))];
}
