namespace ReactorSoftInterlock.Infrastructure.Settings;

public static class RelayBankCommandFactory
{
    public static string BuildTripAllCommandText(RelaySettings settings)
    {
        return string.Join(Environment.NewLine, settings.Channels
            .Where(static channel => channel.Enabled)
            .OrderBy(static channel => channel.ChannelNumber)
            .Select(static channel => channel.OpenCommand));
    }

    public static string BuildRestoreAllCommandText(RelaySettings settings)
    {
        return string.Join(Environment.NewLine, settings.Channels
            .Where(static channel => channel.Enabled)
            .OrderBy(static channel => channel.ChannelNumber)
            .Select(static channel => channel.CloseCommand));
    }

    public static string BuildChannelCommandText(RelaySettings settings, int channelNumber, bool closed)
    {
        var channel = settings.Channels.FirstOrDefault(channel => channel.ChannelNumber == channelNumber)
            ?? throw new InvalidOperationException($"Relay channel {channelNumber} is not configured.");

        return closed ? channel.CloseCommand : channel.OpenCommand;
    }
}
