using ReactorSoftInterlock.Domain;

namespace ReactorSoftInterlock.Tests;

public sealed class InterlockStateMachineTests
{
    [Fact]
    public void DoesNotTripBelowThreshold()
    {
        var machine = new InterlockStateMachine(new InterlockSettings(90.0));

        var decision = machine.Evaluate(89.9);

        Assert.Equal(MonitorStatus.Monitoring, decision.Status);
        Assert.False(decision.ShouldSendStop);
    }

    [Theory]
    [InlineData(90.0)]
    [InlineData(90.1)]
    public void TripsAtOrAboveThreshold(double temperature)
    {
        var machine = new InterlockStateMachine(new InterlockSettings(90.0));

        var decision = machine.Evaluate(temperature);

        Assert.Equal(MonitorStatus.Tripped, decision.Status);
        Assert.True(decision.ShouldSendStop);
        Assert.Equal(RelayAction.StopSent, decision.RelayAction);
    }

    [Fact]
    public void StopCommandIsRequestedOnlyOnceAfterOutputConfirmsIt()
    {
        var machine = new InterlockStateMachine(new InterlockSettings(90.0));

        Assert.True(machine.Evaluate(90.0).ShouldSendStop);
        machine.ConfirmStopSent();
        Assert.False(machine.Evaluate(95.0).ShouldSendStop);
        Assert.False(machine.Evaluate(80.0).ShouldSendStop);
    }

    [Fact]
    public void StopCommandIsRetriedUntilOutputConfirmsIt()
    {
        var machine = new InterlockStateMachine(new InterlockSettings(90.0));

        Assert.True(machine.Evaluate(90.0).ShouldSendStop);
        Assert.True(machine.Evaluate(80.0).ShouldSendStop);

        machine.ConfirmStopSent();

        Assert.False(machine.Evaluate(80.0).ShouldSendStop);
    }

    [Fact]
    public void ResetRequiresValidTemperatureBelowThreshold()
    {
        var machine = new InterlockStateMachine(new InterlockSettings(90.0));
        _ = machine.Evaluate(90.0);

        Assert.False(machine.CanReset(null));
        Assert.False(machine.CanReset(90.0));
        Assert.True(machine.CanReset(89.9));

        machine.Reset(89.9);

        Assert.False(machine.IsTripped);
    }

    [Fact]
    public void ApplySettingsInPlacePreservesConfirmedLatchedTrip()
    {
        var machine = new InterlockStateMachine(new InterlockSettings(90.0));
        Assert.True(machine.Evaluate(95.0).ShouldSendStop);
        machine.ConfirmStopSent();

        machine.ApplySettings(new InterlockSettings(190.0));
        var decision = machine.Evaluate(100.0);

        Assert.True(machine.IsTripped);
        Assert.False(decision.ShouldSendStop);
        Assert.True(machine.CanReset(100.0));
        Assert.Contains("90.0", decision.AlarmReason);
    }

    [Fact]
    public void ReconfigurePreservesUnconfirmedStopRequirement()
    {
        var machine = new InterlockStateMachine(new InterlockSettings(90.0));
        Assert.True(machine.Evaluate(95.0).ShouldSendStop);

        var reconfigured = machine.Reconfigure(new InterlockSettings(190.0));

        Assert.True(reconfigured.Evaluate(80.0).ShouldSendStop);
    }

    [Fact]
    public void ReconfigurePreservesLatchedTripWithoutRequestingSecondStop()
    {
        var machine = new InterlockStateMachine(new InterlockSettings(90.0));
        Assert.True(machine.Evaluate(95.0).ShouldSendStop);
        machine.ConfirmStopSent();

        var reconfigured = machine.Reconfigure(new InterlockSettings(190.0));
        var decision = reconfigured.Evaluate(100.0);

        Assert.True(reconfigured.IsTripped);
        Assert.Equal(MonitorStatus.Tripped, decision.Status);
        Assert.False(decision.ShouldSendStop);
        Assert.Equal(RelayAction.None, decision.RelayAction);
        Assert.True(reconfigured.CanReset(100.0));
        Assert.Contains("90.0", decision.AlarmReason);
    }
}
