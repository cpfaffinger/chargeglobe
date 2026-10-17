using ChargeGlobe.Api.Evses;
using ChargeGlobe.Application.Evses;

namespace ChargeGlobe.Api.Tests;

public class EvseCsvTests
{
    [Fact]
    public void Fields_with_commas_quotes_or_line_breaks_are_quoted()
    {
        var evse = new EvseListItem(
            Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4f"),
            "AT*TST*E1*1",
            new EvseListLocation(Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4e"), "Markt, \"Nord\"", "Linz\nUrfahr", null, null, 48.3, 14.29),
            null,
            "UNKNOWN",
            null,
            null,
            null);

        Assert.Equal(
            "AT*TST*E1*1,01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4e,\"Markt, \"\"Nord\"\"\",\"Linz\nUrfahr\",,,48.3,14.29,,UNKNOWN,,,,",
            EvseSearchEndpoints.CsvLine(evse));
    }
}
