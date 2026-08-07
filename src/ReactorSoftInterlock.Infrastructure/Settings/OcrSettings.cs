namespace ReactorSoftInterlock.Infrastructure.Settings;

public sealed class OcrSettings
{
    public string TesseractExePath { get; set; } = "tesseract.exe";

    public string Language { get; set; } = "eng";

    public int ProcessTimeoutMs { get; set; } = 5000;
}
