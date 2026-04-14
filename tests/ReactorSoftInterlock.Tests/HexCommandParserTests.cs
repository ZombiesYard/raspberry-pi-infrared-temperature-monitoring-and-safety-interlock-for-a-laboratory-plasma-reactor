using ReactorSoftInterlock.Infrastructure.Relay;

namespace ReactorSoftInterlock.Tests;

public sealed class HexCommandParserTests
{
    [Theory]
    [InlineData("A0 01 00 A1", new byte[] { 0xA0, 0x01, 0x00, 0xA1 })]
    [InlineData("A0-01-00-A1", new byte[] { 0xA0, 0x01, 0x00, 0xA1 })]
    [InlineData("A0:01:00:A1", new byte[] { 0xA0, 0x01, 0x00, 0xA1 })]
    public void ParsesCommonHexFormats(string text, byte[] expected)
    {
        Assert.Equal(expected, HexCommandParser.Parse(text));
    }

    [Fact]
    public void RejectsOddNumberOfDigits()
    {
        Assert.Throws<FormatException>(() => HexCommandParser.Parse("A01"));
    }
}
