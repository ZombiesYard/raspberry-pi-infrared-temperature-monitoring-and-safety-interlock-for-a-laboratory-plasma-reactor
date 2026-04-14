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
    public void StopCommandIsRequestedOnlyOnceWhileLatched()
    {
        var machine = new InterlockStateMachine(new InterlockSettings(90.0));

        Assert.True(machine.Evaluate(90.0).ShouldSendStop);
        Assert.False(machine.Evaluate(95.0).ShouldSendStop);
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
}
