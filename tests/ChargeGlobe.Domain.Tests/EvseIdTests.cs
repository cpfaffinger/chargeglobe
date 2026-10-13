using ChargeGlobe.Domain;

namespace ChargeGlobe.Domain.Tests;

public class EvseIdTests
{
    [Theory]
    [InlineData("AT*EVN*E1234*5")]
    [InlineData("AT-EVN-E1234-5")]
    [InlineData("ATEVNE12345")]
    [InlineData("  at*evn*e1234*5 ")]
    [InlineData("AT EVN E1234 5")]
    public void Parse_normalizes_all_supported_notations(string input)
    {
        var evseId = EvseId.Parse(input);

        Assert.Equal("ATEVNE12345", evseId.Normalized);
        Assert.Equal("ATEVNE12345", evseId.ToString());
        Assert.Equal("AT", evseId.CountryCode);
        Assert.Equal("EVN", evseId.OperatorId);
        Assert.Equal("12345", evseId.OutletId);
    }

    [Fact]
    public void Ids_with_the_same_normalized_form_are_equal()
    {
        Assert.Equal(EvseId.Parse("AT*EVN*E1234*5"), EvseId.Parse("atevne12345"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AT*EVN*X1234")]
    [InlineData("A1*EVN*E1234")]
    [InlineData("AT*EVN*E***")]
    [InlineData("+43*811*E1234")]
    public void TryParse_rejects_invalid_input(string? input)
    {
        Assert.False(EvseId.TryParse(input, out var evseId));
        Assert.Null(evseId);
    }

    [Fact]
    public void Parse_throws_on_invalid_input()
    {
        var exception = Assert.Throws<FormatException>(() => EvseId.Parse("nonsense"));

        Assert.Contains("nonsense", exception.Message);
    }
}
