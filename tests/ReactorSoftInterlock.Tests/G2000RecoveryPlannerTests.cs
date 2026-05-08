using ReactorSoftInterlock.Application.G2000;

namespace ReactorSoftInterlock.Tests;

public sealed class G2000RecoveryPlannerTests
{
    [Fact]
    public void HoldHvAus_AlwaysReturnsSafeState()
    {
        var decision = G2000RecoveryPlanner.Plan(
            TripRecoveryPolicy.HoldHvAus,
            new G2000PreTripState
            {
                HvState = G2000HvState.HvOn,
                Setpoints = new G2000WritableSetpoints { VoltageV = 63.0 }
            });

        Assert.Equal(G2000HvState.HvAus, decision.HvState);
        Assert.Equal(63.0, decision.Setpoints.VoltageV);
        Assert.False(decision.ResumeAutomaticSequence);
    }

    [Fact]
    public void RestoreHvReady_ReturnsReadyWithoutResumingAutomaticSequence()
    {
        var decision = G2000RecoveryPlanner.Plan(
            TripRecoveryPolicy.RestoreHvReady,
            new G2000PreTripState
            {
                HvState = G2000HvState.HvOn,
                UiMode = G2000UiMode.Automatic
            });

        Assert.Equal(G2000HvState.HvReady, decision.HvState);
        Assert.Equal(G2000UiMode.Manual, decision.UiMode);
        Assert.False(decision.ResumeAutomaticSequence);
    }

    [Fact]
    public void RestorePreviousState_ReusesCapturedAutomaticContext()
    {
        var decision = G2000RecoveryPlanner.Plan(
            TripRecoveryPolicy.RestorePreviousState,
            new G2000PreTripState
            {
                HvState = G2000HvState.HvOn,
                UiMode = G2000UiMode.Automatic,
                AutomaticSequenceActive = true,
                AutomaticStage = "Stage2",
                Setpoints = new G2000WritableSetpoints { VoltageV = 43.0 }
            });

        Assert.Equal(G2000HvState.HvOn, decision.HvState);
        Assert.Equal(G2000UiMode.Automatic, decision.UiMode);
        Assert.True(decision.ResumeAutomaticSequence);
        Assert.Equal("Stage2", decision.AutomaticStage);
        Assert.Equal(43.0, decision.Setpoints.VoltageV);
    }
}
