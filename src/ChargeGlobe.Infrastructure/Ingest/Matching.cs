using ChargeGlobe.Domain.Provenance;

namespace ChargeGlobe.Infrastructure.Ingest;

/// <summary>
/// Which source may set an attribute (docs/03, "Präzedenzen"): the stronger tier of the attribute group wins and takes an
/// attribute over from a weaker owner; equal tiers keep the first owner. Tiers stand for the kinds of sources of the
/// table: <c>PARTNER</c> = CPO directly, <c>OFFICIAL</c> = official register/NAP, <c>APP_BACKEND</c> and
/// <c>PUBLIC_WEB</c> = apps and web maps of EMPs, <c>COMMUNITY</c> = Open Charge Map, <c>OPEN_DATA</c> = OpenStreetMap.
/// </summary>
public static class AttributePrecedence
{
    // Strongest first.
    private static readonly SourceTier[] Position =
        [SourceTier.PARTNER, SourceTier.OFFICIAL, SourceTier.OPEN_DATA, SourceTier.COMMUNITY, SourceTier.APP_BACKEND, SourceTier.PUBLIC_WEB];

    private static readonly SourceTier[] Technology =
        [SourceTier.PARTNER, SourceTier.OFFICIAL, SourceTier.APP_BACKEND, SourceTier.PUBLIC_WEB, SourceTier.COMMUNITY, SourceTier.OPEN_DATA];

    private static readonly SourceTier[] MasterData =
        [SourceTier.OFFICIAL, SourceTier.PARTNER, SourceTier.APP_BACKEND, SourceTier.PUBLIC_WEB, SourceTier.COMMUNITY, SourceTier.OPEN_DATA];

    /// <summary>Rank of the tier for the attribute, higher wins; 0 for a source without registry entry.</summary>
    public static int Rank(SourceTier? tier, string attribute)
    {
        var order = attribute switch
        {
            "geom" => Position,
            "max_power_kw" or "power_type" or "connectors" => Technology,
            _ => MasterData,
        };
        return tier is { } known ? order.Length - Array.IndexOf(order, known) : 0;
    }
}
