using System.Text.Json;
using ReactorSoftInterlock.Infrastructure.Logging;

namespace ReactorSoftInterlock.Tests;

public sealed class LabProfileStoreTests
{
    [Fact]
    public async Task LoadOrCreateAsync_PrefillsKnownFactsAndPersistsUnknownsExplicitly()
    {
        var root = CreateTemporaryDirectory();

        var profile = await LabProfileStore.LoadOrCreateAsync(root, CancellationToken.None);

        Assert.Equal("Yu Zhang", profile.Operator);
        Assert.Contains("M.Sc. Shukang Zhang", profile.Supervisors);
        Assert.Equal("HIKMICRO E20Plus", profile.Camera.Model);
        Assert.Equal(0.40, profile.Camera.DistanceM);
        Assert.Equal("HikmicroAnalyzer Max overlay", profile.Camera.MeasurementSource);
        Assert.Equal("DSD TECH SH-UR04A 4CH", profile.Relay.Model);
        Assert.Equal("normally_closed", profile.Relay.InterlockContactMode);
        Assert.Equal("I1-I2", profile.Relay.ChannelMappings["CH1"]);
        Assert.Equal("I7-I8", profile.Relay.ChannelMappings["CH4"]);
        Assert.Equal("ceramic", profile.Reactor.PlasmaContactBaseMaterial);
        Assert.Equal(195.0, profile.Reactor.HistoricalEmpiricalFailureTemperatureC);
        Assert.Equal(160.0, profile.Reactor.HighestObservedTemperatureC);
        Assert.Equal("not_confirmed", profile.Reactor.HistoricalDamageMechanism);
        Assert.Equal("not_recorded", profile.Camera.Emissivity);
        Assert.Equal("not_recorded", profile.Calibration.ReferenceThermometer);
        Assert.Equal("not_recorded", profile.Process.GasSpecies);

        var path = Path.Combine(root, "lab-profile.json");
        Assert.True(File.Exists(path));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal("Yu Zhang", json.RootElement.GetProperty("Operator").GetString());
    }

    [Fact]
    public async Task LoadOrCreateAsync_PreservesAnEditedMasterProfile()
    {
        var root = CreateTemporaryDirectory();
        var profile = await LabProfileStore.LoadOrCreateAsync(root, CancellationToken.None);
        profile.Process.GasSpecies = "argon";
        await LabProfileStore.SaveAsync(root, profile, CancellationToken.None);

        var reloaded = await LabProfileStore.LoadOrCreateAsync(root, CancellationToken.None);

        Assert.Equal("argon", reloaded.Process.GasSpecies);
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
