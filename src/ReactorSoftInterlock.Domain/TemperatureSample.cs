namespace ReactorSoftInterlock.Domain;

public sealed record TemperatureSample(
    DateTimeOffset Timestamp,
    double? TemperatureC,
    string RawOcrText,
    MonitorStatus Status,
    string AlarmReason,
    RelayAction RelayAction,
    string ScreenshotRoi);
