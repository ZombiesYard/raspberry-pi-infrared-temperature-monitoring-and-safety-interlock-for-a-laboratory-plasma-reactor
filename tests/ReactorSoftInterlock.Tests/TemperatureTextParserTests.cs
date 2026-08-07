using ReactorSoftInterlock.Application;

namespace ReactorSoftInterlock.Tests;

public sealed class TemperatureTextParserTests
{
    private readonly TemperatureTextParser _parser = new();

    [Theory]
    [InlineData("Max 89.9°C", 89.9)]
    [InlineData("Max 90.1 C", 90.1)]
    [InlineData("最高 90.0 C", 90.0)]
    [InlineData("Spot 72.2 C\nMax 91.4 C", 91.4)]
    public void ParsesHighestTemperature(string text, double expected)
    {
        Assert.Equal(expected, _parser.ParseHighestTemperatureC(text));
    }

    [Theory]
    [InlineData(-50.0, true)]
    [InlineData(300.0, true)]
    [InlineData(-50.1, false)]
    [InlineData(300.1, false)]
    [InlineData(0.0, false)]
    [InlineData(double.NaN, false)]
    public void PlausibilityRangeMatchesConfigurableSafetyRange(double value, bool expected)
    {
        Assert.Equal(expected, TemperatureTextParser.IsPlausibleReading(value));
    }

    [Fact]
    public void ReturnsNullWhenNoTemperatureIsVisible()
    {
        Assert.Null(_parser.ParseHighestTemperatureC("NO READING"));
    }
}
