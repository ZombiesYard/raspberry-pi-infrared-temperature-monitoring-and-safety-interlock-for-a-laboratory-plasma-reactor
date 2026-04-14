namespace ReactorSoftInterlock.Application;

public sealed record TemperatureReading(double? TemperatureC, string RawText, string RoiDescription);
