using ReactorSoftInterlock.Infrastructure.Settings;

namespace ReactorSoftInterlock.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public async Task LoadAsyncPopulatesRelayBankDefaultsForLegacyConfig()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(
                tempFile,
                """
                {
                  "Language": "en",
                  "Relay": {
                    "DryRun": false,
                    "PortName": "COM7",
                    "BaudRate": 9600,
                    "StopCommandHex": "AT+CH1=0",
                    "ResetCommandHex": "AT+CH1=1",
                    "OpenOnAlarm": true
                  }
                }
                """);

            var store = new SettingsStore(tempFile);

            var settings = await store.LoadAsync(CancellationToken.None);

            Assert.Equal(4, settings.Relay.Channels.Count);
            Assert.Equal("COM7", settings.Relay.PortName);
            Assert.Equal("AT+CH1=0", settings.Relay.Channels[0].OpenCommand);
            Assert.Equal("AT+CH4=1", settings.Relay.Channels[3].CloseCommand);
            Assert.Contains("AT+CH4=0", settings.Relay.StopCommandHex);
            Assert.Contains("AT+CH4=1", settings.Relay.ResetCommandHex);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
