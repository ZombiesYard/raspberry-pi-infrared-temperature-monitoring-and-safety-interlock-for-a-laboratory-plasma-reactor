namespace ReactorSoftInterlock.Infrastructure.Settings;

public sealed class RelaySettings
{
    public bool DryRun { get; set; } = true;

    public string PortName { get; set; } = "COM3";

    public int BaudRate { get; set; } = 9600;

    public string StopCommandHex { get; set; } = string.Empty;

    public string ResetCommandHex { get; set; } = string.Empty;

    public bool OpenOnAlarm { get; set; } = true;
}
