namespace ReactorSoftInterlock.Infrastructure.Settings;

public sealed class RelaySettings
{
    public bool DryRun { get; set; } = true;

    public string PortName { get; set; } = "COM3";

    public int BaudRate { get; set; } = 9600;

    public string StopCommandHex { get; set; } = string.Empty;

    public string ResetCommandHex { get; set; } = string.Empty;

    public bool OpenOnAlarm { get; set; } = true;

    public List<RelayChannelSettings> Channels { get; set; } = CreateDefaultChannels();

    public void Normalize()
    {
        var existing = (Channels ?? [])
            .Where(static channel => channel.ChannelNumber is >= 1 and <= 4)
            .GroupBy(static channel => channel.ChannelNumber)
            .ToDictionary(static group => group.Key, static group => group.First());

        Channels = CreateDefaultChannels()
            .Select(defaultChannel =>
            {
                if (!existing.TryGetValue(defaultChannel.ChannelNumber, out var channel))
                {
                    return defaultChannel;
                }

                channel.DisplayName = string.IsNullOrWhiteSpace(channel.DisplayName) ? defaultChannel.DisplayName : channel.DisplayName;
                channel.InterlockMapping = string.IsNullOrWhiteSpace(channel.InterlockMapping) ? defaultChannel.InterlockMapping : channel.InterlockMapping;
                channel.OpenCommand = string.IsNullOrWhiteSpace(channel.OpenCommand) ? defaultChannel.OpenCommand : channel.OpenCommand;
                channel.CloseCommand = string.IsNullOrWhiteSpace(channel.CloseCommand) ? defaultChannel.CloseCommand : channel.CloseCommand;
                return channel;
            })
            .OrderBy(static channel => channel.ChannelNumber)
            .ToList();

        StopCommandHex = RelayBankCommandFactory.BuildTripAllCommandText(this);
        ResetCommandHex = RelayBankCommandFactory.BuildRestoreAllCommandText(this);
    }

    private static List<RelayChannelSettings> CreateDefaultChannels()
    {
        return
        [
            new RelayChannelSettings
            {
                ChannelNumber = 1,
                DisplayName = "CH1",
                InterlockMapping = "Interlock A negative (I1-I2)",
                Enabled = true,
                OpenCommand = "AT+CH1=0",
                CloseCommand = "AT+CH1=1"
            },
            new RelayChannelSettings
            {
                ChannelNumber = 2,
                DisplayName = "CH2",
                InterlockMapping = "Interlock A positive (I5-I6)",
                Enabled = true,
                OpenCommand = "AT+CH2=0",
                CloseCommand = "AT+CH2=1"
            },
            new RelayChannelSettings
            {
                ChannelNumber = 3,
                DisplayName = "CH3",
                InterlockMapping = "Interlock B negative (I3-I4)",
                Enabled = true,
                OpenCommand = "AT+CH3=0",
                CloseCommand = "AT+CH3=1"
            },
            new RelayChannelSettings
            {
                ChannelNumber = 4,
                DisplayName = "CH4",
                InterlockMapping = "Interlock B positive (I7-I8)",
                Enabled = true,
                OpenCommand = "AT+CH4=0",
                CloseCommand = "AT+CH4=1"
            }
        ];
    }
}
