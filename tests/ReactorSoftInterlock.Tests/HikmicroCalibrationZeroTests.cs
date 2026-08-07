using ReactorSoftInterlock.Application;

namespace ReactorSoftInterlock.Tests;

public sealed class HikmicroCalibrationZeroTests
{
    [Theory]
    [InlineData("Max: 0.0 C")]
    [InlineData("Max: -0.0 °C")]
    [InlineData("Max: 0.0 C ROI 25")]
    public void ExactCalibrationZeroIsNotAValidTemperature(string rawText)
    {
        var parser = new TemperatureTextParser();

        var temperature = parser.ParseHighestTemperatureC(rawText);

        Assert.Null(temperature);
    }
}
