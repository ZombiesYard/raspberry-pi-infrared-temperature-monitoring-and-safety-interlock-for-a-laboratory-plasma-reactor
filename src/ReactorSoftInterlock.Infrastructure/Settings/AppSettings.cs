namespace ReactorSoftInterlock.Infrastructure.Settings;

public sealed class AppSettings
{
    public string Language { get; set; } = "en";

    public double ThresholdC { get; set; } = 90.0;

    public bool AutoResetEnabled { get; set; } = true;

    public double RecoveryThresholdC { get; set; } = 85.0;

    public int RecoveryStableSeconds { get; set; } = 30;

    public int PollIntervalMs { get; set; } = 1000;

    public string WindowTitleContains { get; set; } = "Hikmicro";

    public RoiSettings Roi { get; set; } = new();

    public OcrSettings Ocr { get; set; } = new();

    public RelaySettings Relay { get; set; } = new();

    public Amc2100Settings Amc2100 { get; set; } = new();

    public ExperimentUploadSettings ExperimentUpload { get; set; } = new();

    public string DataDirectory { get; set; } = "data";

    public AppSettings Clone()
    {
        return new AppSettings
        {
            Language = Language,
            ThresholdC = ThresholdC,
            AutoResetEnabled = AutoResetEnabled,
            RecoveryThresholdC = RecoveryThresholdC,
            RecoveryStableSeconds = RecoveryStableSeconds,
            PollIntervalMs = PollIntervalMs,
            WindowTitleContains = WindowTitleContains,
            Roi = new RoiSettings
            {
                X = Roi.X,
                Y = Roi.Y,
                Width = Roi.Width,
                Height = Roi.Height
            },
            Ocr = new OcrSettings
            {
                TesseractExePath = Ocr.TesseractExePath,
                Language = Ocr.Language,
                ProcessTimeoutMs = Ocr.ProcessTimeoutMs
            },
            Relay = Relay.Clone(),
            Amc2100 = Amc2100.Clone(),
            ExperimentUpload = new ExperimentUploadSettings
            {
                AutoUploadEnabled = ExperimentUpload.AutoUploadEnabled,
                BaseUrl = ExperimentUpload.BaseUrl,
                ProjectId = ExperimentUpload.ProjectId,
                PackageName = ExperimentUpload.PackageName,
                CredentialTarget = ExperimentUpload.CredentialTarget,
                HttpTimeoutSeconds = ExperimentUpload.HttpTimeoutSeconds
            },
            DataDirectory = DataDirectory
        };
    }
}
