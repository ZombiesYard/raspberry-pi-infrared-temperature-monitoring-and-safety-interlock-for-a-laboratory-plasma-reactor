using ReactorSoftInterlock.Infrastructure.Settings;

namespace ReactorSoftInterlock.Tests;

public sealed class RelayBankCommandFactoryTests
{
    [Fact]
    public void NormalizeBuildsDefaultDsdChannels()
    {
        var settings = new RelaySettings
        {
            Channels = []
        };

        settings.Normalize();

        Assert.Equal(4, settings.Channels.Count);
        Assert.Equal("AT+CH1=0", settings.Channels[0].OpenCommand);
        Assert.Equal("AT+CH1=1", settings.Channels[0].CloseCommand);
        Assert.Equal("AT+CH4=0", settings.Channels[3].OpenCommand);
        Assert.Equal("AT+CH4=1", settings.Channels[3].CloseCommand);
    }

    [Fact]
    public void BuildTripAllCommandTextUsesEnabledChannelsInOrder()
    {
        var settings = new RelaySettings();
        settings.Normalize();
        settings.Channels[1].Enabled = false;

        var commandText = RelayBankCommandFactory.BuildTripAllCommandText(settings);

        Assert.Equal("AT+CH1=0\nAT+CH3=0\nAT+CH4=0".Replace("\n", Environment.NewLine), commandText);
    }

    [Fact]
    public void BuildRestoreAllCommandTextUsesEnabledChannelsInOrder()
    {
        var settings = new RelaySettings();
        settings.Normalize();

        var commandText = RelayBankCommandFactory.BuildRestoreAllCommandText(settings);

        Assert.Equal("AT+CH1=1\nAT+CH2=1\nAT+CH3=1\nAT+CH4=1".Replace("\n", Environment.NewLine), commandText);
    }

    [Fact]
    public void BuildChannelCommandTextReturnsConfiguredCommands()
    {
        var settings = new RelaySettings();
        settings.Normalize();
        settings.Channels[2].OpenCommand = "AT+CH3=0";
        settings.Channels[2].CloseCommand = "AT+CH3=1";

        Assert.Equal("AT+CH3=0", RelayBankCommandFactory.BuildChannelCommandText(settings, 3, closed: false));
        Assert.Equal("AT+CH3=1", RelayBankCommandFactory.BuildChannelCommandText(settings, 3, closed: true));
    }
}
