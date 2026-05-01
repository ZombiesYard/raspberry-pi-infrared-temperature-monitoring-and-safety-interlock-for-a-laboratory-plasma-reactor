namespace ReactorSoftInterlock.Infrastructure.Settings;

public sealed class RelayChannelSettings
{
    public int ChannelNumber { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string InterlockMapping { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public string OpenCommand { get; set; } = string.Empty;

    public string CloseCommand { get; set; } = string.Empty;
}
