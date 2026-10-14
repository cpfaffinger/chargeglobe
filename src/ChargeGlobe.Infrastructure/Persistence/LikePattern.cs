namespace ChargeGlobe.Infrastructure.Persistence;

/// <summary>Patterns for <c>LIKE</c>/<c>ILIKE</c> with the default escape character <c>\</c>.</summary>
public static class LikePattern
{
    /// <summary><c>%text%</c> with <c>\</c>, <c>%</c> and <c>_</c> of the text taken literally.</summary>
    public static string Contains(string text) =>
        $"%{text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";
}
