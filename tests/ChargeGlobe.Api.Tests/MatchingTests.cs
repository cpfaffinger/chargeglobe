using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Infrastructure.Ingest;

namespace ChargeGlobe.Api.Tests;

public class MatchingTests
{
    [Fact]
    public void The_official_register_ranks_above_the_community_and_OpenStreetMap_depends_on_the_attribute()
    {
        Assert.True(AttributePrecedence.Rank(SourceTier.OFFICIAL, "name") > AttributePrecedence.Rank(SourceTier.COMMUNITY, "name"));
        Assert.True(AttributePrecedence.Rank(SourceTier.OFFICIAL, "geom") > AttributePrecedence.Rank(SourceTier.COMMUNITY, "geom"));
        Assert.True(AttributePrecedence.Rank(SourceTier.OFFICIAL, "connectors") > AttributePrecedence.Rank(SourceTier.COMMUNITY, "connectors"));
        // Position: CPO > official > OpenStreetMap > Open Charge Map; name: Open Charge Map > OpenStreetMap.
        Assert.True(AttributePrecedence.Rank(SourceTier.PARTNER, "geom") > AttributePrecedence.Rank(SourceTier.OFFICIAL, "geom"));
        Assert.True(AttributePrecedence.Rank(SourceTier.OPEN_DATA, "geom") > AttributePrecedence.Rank(SourceTier.COMMUNITY, "geom"));
        Assert.True(AttributePrecedence.Rank(SourceTier.COMMUNITY, "address") > AttributePrecedence.Rank(SourceTier.OPEN_DATA, "address"));
        Assert.True(AttributePrecedence.Rank(SourceTier.APP_BACKEND, "max_power_kw") > AttributePrecedence.Rank(SourceTier.COMMUNITY, "max_power_kw"));
        Assert.True(AttributePrecedence.Rank(SourceTier.PUBLIC_WEB, "power_type") > AttributePrecedence.Rank(SourceTier.OPEN_DATA, "power_type"));
        Assert.Equal(0, AttributePrecedence.Rank(null, "name"));
        Assert.Equal(1, AttributePrecedence.Rank(SourceTier.OPEN_DATA, "name"));
    }

    [Theory]
    [InlineData("Wien Energie GmbH", "TANKE Wien Energie", true)]
    [InlineData("SMATRICS GmbH & Co KG", "SMATRICS Netz", true)]
    [InlineData("Tesla (Tesla-only charging)", "Tesla Germany GmbH", true)]
    [InlineData("EVN", "EVN Energievertrieb GmbH & Co KG", true)]
    [InlineData("Wien Energie GmbH", "EVN Energievertrieb GmbH", false)]
    [InlineData("GmbH", "Wien Energie", false)]
    [InlineData(null, "Wien Energie", false)]
    public void Operator_names_match_without_legal_forms(string? left, string? right, bool same)
    {
        Assert.Equal(same, OperatorNames.Same(left, right));
        Assert.Equal(same, OperatorNames.Same(right, left));
    }
}
