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
        Assert.Equal(evseId.Normalized, evseId.Display.Replace("*", string.Empty));
        Assert.Equal("AT", evseId.CountryCode);
        Assert.Equal("EVN", evseId.OperatorId);
        Assert.Equal("12345", evseId.OutletId);
    }

    [Fact]
    public void Ids_with_the_same_normalized_form_are_equal()
    {
        var separated = EvseId.Parse("AT-EVN-E1234-5");
        var compact = EvseId.Parse("atevne12345");

        Assert.Equal(separated, compact);
        Assert.Equal(separated.GetHashCode(), compact.GetHashCode());
        Assert.Equal("AT*EVN*E1234*5", separated.Display);
        Assert.Equal("ATEVNE12345", compact.Display);
        Assert.NotEqual(separated, EvseId.Parse("AT*EVN*E1234*6"));
        Assert.False(separated.Equals(null));
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
