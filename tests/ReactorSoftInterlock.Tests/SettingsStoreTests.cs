using ReactorSoftInterlock.Infrastructure.Settings;

namespace ReactorSoftInterlock.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public void DefaultsKeepTemperatureMonitorAtOneHertz()
    {
        var settings = new AppSettings();

        Assert.Equal(1000, settings.PollIntervalMs);
    }

    [Fact]
    public void Amc2100CloneIsIndependent()
    {
        var original = new Amc2100Settings { PortName = "COM7", FallbackRestoreSetpointMlMin = 600 };

        var clone = original.Clone();
        clone.PortName = "COM8";
        clone.FallbackRestoreSetpointMlMin = 700;

        Assert.Equal("COM7", original.PortName);
        Assert.Equal(600, original.FallbackRestoreSetpointMlMin);
    }

    [Fact]
    public async Task ConcurrentSavesDoNotCorruptSettingsFile()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var store = new SettingsStore(tempFile);
            var saves = Enumerable.Range(1, 20)
                .Select(index => store.SaveAsync(
                    new AppSettings { PollIntervalMs = 1000 + index },
                    CancellationToken.None));

            await Task.WhenAll(saves);
            var loaded = await store.LoadAsync(CancellationToken.None);

            Assert.InRange(loaded.PollIntervalMs, 1001, 1020);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

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
            Assert.Equal("COM4", settings.Amc2100.PortName);
            Assert.Equal(19200, settings.Amc2100.BaudRate);
            Assert.Equal(1, settings.Amc2100.SlaveAddress);
            Assert.True(settings.ExperimentUpload.AutoUploadEnabled);
            Assert.Equal("https://gitlab.tu-clausthal.de", settings.ExperimentUpload.BaseUrl);
            Assert.Equal(5539, settings.ExperimentUpload.ProjectId);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task SaveAsyncPersistsUploadTargetButHasNoCredentialField()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var settings = new AppSettings();
            settings.ExperimentUpload.PackageName = "reactor-experiment-evidence";
            await new SettingsStore(tempFile).SaveAsync(settings, CancellationToken.None);

            var json = await File.ReadAllTextAsync(tempFile);
            Assert.Contains("ReactorSoftInterlock/GitLab/gitlab.tu-clausthal.de/5539", json);
            Assert.DoesNotContain("PersonalAccessToken", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PrivateToken", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Password", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void UploadSettingsBindCredentialToNormalizedHostAndProject()
    {
        var settings = new ExperimentUploadSettings
        {
            BaseUrl = "https://GITLAB.EXAMPLE.test:8443/root/",
            ProjectId = 77,
            CredentialTarget = "ReactorSoftInterlock/GitLab/old-host/77"
        };

        settings.Normalize();

        Assert.Equal("https://gitlab.example.test:8443/root", settings.BaseUrl);
        Assert.Equal(
            "ReactorSoftInterlock/GitLab/gitlab.example.test:8443/77",
            settings.CredentialTarget);
    }

    [Theory]
    [InlineData("http://gitlab.example.test")]
    [InlineData("https://user:password@gitlab.example.test")]
    [InlineData("https://gitlab.example.test?redirect=evil")]
    [InlineData("https://gitlab.example.test/#fragment")]
    public void UploadSettingsRejectInsecureOrAmbiguousBaseUrls(string value)
    {
        Assert.False(ExperimentUploadSettings.TryGetSecureBaseUri(value, out _));
        var settings = new ExperimentUploadSettings { BaseUrl = value, AutoUploadEnabled = true };

        settings.Normalize();

        Assert.False(settings.AutoUploadEnabled);
        Assert.Equal(value, settings.BaseUrl);
        Assert.Empty(settings.CredentialTarget);
    }

    [Theory]
    [InlineData(0, "reactor-experiment-evidence")]
    [InlineData(5539, "")]
    public void UploadSettingsDisableRatherThanRetargetInvalidProjectOrPackage(
        int projectId,
        string packageName)
    {
        var settings = new ExperimentUploadSettings
        {
            AutoUploadEnabled = true,
            ProjectId = projectId,
            PackageName = packageName
        };

        settings.Normalize();

        Assert.False(settings.AutoUploadEnabled);
        Assert.Equal(projectId, settings.ProjectId);
        Assert.Equal(packageName, settings.PackageName);
        Assert.Empty(settings.CredentialTarget);
    }
}
