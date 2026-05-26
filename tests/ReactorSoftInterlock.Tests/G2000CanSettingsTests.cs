using ReactorSoftInterlock.Application.G2000;
using ReactorSoftInterlock.Infrastructure.Settings;

namespace ReactorSoftInterlock.Tests;

public sealed class G2000CanSettingsTests
{
    [Fact]
    public void ValidateWritableSetpoints_AllowsPanelRangeByDefault()
    {
        var settings = new G2000CanSettings
        {
            VerifiedU2MinVoltageV = 0.0,
            VerifiedU2MaxVoltageV = 300.0,
            AllowUnsafeU2Writes = false
        };

        settings.ValidateWritableSetpoints(new G2000WritableSetpoints { VoltageV = 3.0 });
    }

    [Fact]
    public void ValidateWritableSetpoints_BlocksValuesOutsideManualRange()
    {
        var settings = new G2000CanSettings
        {
            VerifiedU2MinVoltageV = 0.0,
            VerifiedU2MaxVoltageV = 300.0,
            AllowUnsafeU2Writes = false
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            settings.ValidateWritableSetpoints(new G2000WritableSetpoints { VoltageV = 301.0 }));

        Assert.Contains("U2", ex.Message);
        Assert.Contains("300", ex.Message);
    }

    [Fact]
    public void ValidateWritableSetpoints_AllowsOutOfRangeWhenExplicitlyEnabled()
    {
        var settings = new G2000CanSettings
        {
            VerifiedU2MinVoltageV = 0.0,
            VerifiedU2MaxVoltageV = 300.0,
            AllowUnsafeU2Writes = true
        };

        settings.ValidateWritableSetpoints(new G2000WritableSetpoints { VoltageV = 301.0 });
    }

    [Fact]
    public void ValidateStartupRecipe_BlocksUnverifiedStageVoltages()
    {
        var settings = new G2000CanSettings
        {
            VerifiedU2MinVoltageV = 0.0,
            VerifiedU2MaxVoltageV = 300.0
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            settings.ValidateStartupRecipe(new G2000StartupRecipe
            {
                Stage1VoltageV = 63.0,
                Stage2VoltageV = 430.0
            }));

        Assert.Contains(nameof(G2000StartupRecipe.Stage2VoltageV), ex.Message);
    }

    [Fact]
    public void Clone_PreservesTimingAndSafetyFields()
    {
        var settings = new G2000CanSettings
        {
            Channel = "UsbBus2",
            NodeId = 5,
            CommandPeriodMs = 150,
            ReadPollIntervalMs = 30,
            UiMode = nameof(G2000UiMode.Automatic),
            RecoveryPolicy = nameof(TripRecoveryPolicy.RestorePreviousState),
            VerifiedU2MinVoltageV = 10.0,
            VerifiedU2MaxVoltageV = 80.0,
            AllowUnsafeU2Writes = true,
            HvReadyLeadTimeMs = 5000
        };

        var clone = settings.Clone();

        Assert.Equal("UsbBus2", clone.Channel);
        Assert.Equal((byte)5, clone.NodeId);
        Assert.Equal(150, clone.CommandPeriodMs);
        Assert.Equal(30, clone.ReadPollIntervalMs);
        Assert.Equal(nameof(G2000UiMode.Automatic), clone.UiMode);
        Assert.Equal(nameof(TripRecoveryPolicy.RestorePreviousState), clone.RecoveryPolicy);
        Assert.Equal(10.0, clone.VerifiedU2MinVoltageV);
        Assert.Equal(80.0, clone.VerifiedU2MaxVoltageV);
        Assert.True(clone.AllowUnsafeU2Writes);
        Assert.Equal(5000, clone.HvReadyLeadTimeMs);
    }
}
