using ReactorSoftInterlock.Infrastructure.Relay;

namespace ReactorSoftInterlock.Tests;

public sealed class RelayCommandTextParserTests
{
    [Theory]
    [InlineData("AT+CH1=0", new[] { "AT+CH1=0" })]
    [InlineData("AT+CH1=0;AT+CH2=0", new[] { "AT+CH1=0", "AT+CH2=0" })]
    [InlineData("AT+CH1=0\r\nAT+CH2=1", new[] { "AT+CH1=0", "AT+CH2=1" })]
    public void SplitsAsciiRelayCommands(string text, string[] expected)
    {
        Assert.Equal(expected, RelayCommandTextParser.Parse(text));
    }

    [Fact]
    public void ReturnsEmptyForWhitespace()
    {
        Assert.Empty(RelayCommandTextParser.Parse(" \r\n "));
    }
}
