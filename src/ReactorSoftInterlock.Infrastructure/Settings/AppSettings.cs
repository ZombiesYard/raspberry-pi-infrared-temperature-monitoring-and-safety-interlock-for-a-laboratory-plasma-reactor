namespace ReactorSoftInterlock.Infrastructure.Settings;

public sealed class AppSettings
{
    public double ThresholdC { get; set; } = 90.0;

    public int PollIntervalMs { get; set; } = 1000;

    public string WindowTitleContains { get; set; } = "Hikmicro";

    public RoiSettings Roi { get; set; } = new();

    public OcrSettings Ocr { get; set; } = new();

    public RelaySettings Relay { get; set; } = new();

    public string DataDirectory { get; set; } = "data";
}
