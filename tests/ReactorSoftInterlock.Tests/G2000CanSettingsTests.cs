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
}
