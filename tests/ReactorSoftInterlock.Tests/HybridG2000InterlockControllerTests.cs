using ReactorSoftInterlock.Application.G2000;
using ReactorSoftInterlock.Application.Ports;
using ReactorSoftInterlock.Domain;
using ReactorSoftInterlock.Infrastructure.Relay;

namespace ReactorSoftInterlock.Tests;

public sealed class HybridG2000InterlockControllerTests
{
    [Fact]
    public async Task StopAsync_OpensOnlyPhysicalInterlock()
    {
        var events = new List<string>();
        using var controller = new HybridG2000InterlockController(new FakeG2000Controller(events), new FakePhysicalRelay(events));

        await controller.StopAsync(CancellationToken.None);

        Assert.Equal(["g2000-latch-Temperature limit trip", "physical-open-all"], events);
    }

    [Fact]
    public async Task ResetAsync_MakesG2000SafeBeforeClosingPhysicalInterlock()
    {
        var events = new List<string>();
        using var controller = new HybridG2000InterlockController(new FakeG2000Controller(events), new FakePhysicalRelay(events));

        await controller.ResetAsync(CancellationToken.None);

        Assert.Equal(["g2000-set-HvAus", "physical-close-all", "g2000-reset"], events);
    }

    [Fact]
    public async Task ResetAsync_WhenG2000TripLatched_PreservesRecoveryPolicyUntilReset()
    {
        var events = new List<string>();
        using var controller = new HybridG2000InterlockController(new FakeG2000Controller(events, isTripLatched: true), new FakePhysicalRelay(events));

        await controller.ResetAsync(CancellationToken.None);

        Assert.Equal(["g2000-prepare-recovery", "physical-close-all", "g2000-complete-recovery"], events);
    }

    [Fact]
    public async Task StopThenReset_UsesLatchedG2000Recovery()
    {
        var events = new List<string>();
        using var controller = new HybridG2000InterlockController(new FakeG2000Controller(events), new FakePhysicalRelay(events));

        await controller.StopAsync(CancellationToken.None);
        await controller.ResetAsync(CancellationToken.None);

        Assert.Equal(["g2000-latch-Temperature limit trip", "physical-open-all", "g2000-prepare-recovery", "physical-close-all", "g2000-complete-recovery"], events);
    }

    [Fact]
    public async Task StartAutomaticSequenceAsync_ForwardsToG2000Only()
    {
        var events = new List<string>();
        using var controller = new HybridG2000InterlockController(new FakeG2000Controller(events), new FakePhysicalRelay(events));

        await controller.StartAutomaticSequenceAsync(new G2000StartupRecipe(), CancellationToken.None);

        Assert.Equal(["g2000-start-automatic"], events);
    }

    private sealed class FakeG2000Controller(List<string> events, bool isTripLatched = false) : IG2000Controller, IG2000TripLatch, IG2000RecoveryPreparation
    {
        private bool _isTripLatched = isTripLatched;

        public event EventHandler<G2000TelemetrySnapshot>? TelemetryUpdated
        {
            add { }
            remove { }
        }

        public G2000TelemetrySnapshot Snapshot { get; } = new();
        public G2000WritableSetpoints TargetSetpoints { get; } = new();
        public G2000StartupRecipe StartupRecipe { get; } = new();
        public TripRecoveryPolicy RecoveryPolicy { get; set; }
        public G2000UiMode UiMode { get; set; }
        public bool IsTripLatched => _isTripLatched;
        public void LatchSoftwareTrip(string reason)
        {
            _isTripLatched = true;
            events.Add($"g2000-latch-{reason}");
        }
        public Task PrepareRecoveryWhileInterlockOpenAsync(CancellationToken cancellationToken)
        {
            _isTripLatched = false;
            events.Add("g2000-prepare-recovery");
            return Task.CompletedTask;
        }
        public Task CompletePreparedRecoveryAfterInterlockClosedAsync(CancellationToken cancellationToken)
        {
            events.Add("g2000-complete-recovery");
            return Task.CompletedTask;
        }
        public Task EnsureConnectedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetHvStateAsync(G2000HvState state, CancellationToken cancellationToken)
        {
            events.Add($"g2000-set-{state}");
            return Task.CompletedTask;
        }
        public Task ApplyWritableSetpointsAsync(G2000WritableSetpoints setpoints, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartAutomaticSequenceAsync(G2000StartupRecipe recipe, CancellationToken cancellationToken)
        {
            events.Add("g2000-start-automatic");
            return Task.CompletedTask;
        }

        public Task StopAutomaticSequenceAsync(CancellationToken cancellationToken)
        {
            events.Add("g2000-stop-automatic");
            return Task.CompletedTask;
        }

        public Task<RelayAction> StopAsync(CancellationToken cancellationToken)
        {
            events.Add("g2000-stop");
            return Task.FromResult(RelayAction.StopSent);
        }

        public Task<RelayAction> ResetAsync(CancellationToken cancellationToken)
        {
            events.Add("g2000-reset");
            return Task.FromResult(RelayAction.ResetSent);
        }

        public Task<RelayAction> TestStopAsync(CancellationToken cancellationToken)
        {
            events.Add("g2000-test-stop");
            return Task.FromResult(RelayAction.TestStopSent);
        }

        public Task OpenAllInterlocksAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CloseAllInterlocksAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetChannelClosedAsync(int channelNumber, bool closed, CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class FakePhysicalRelay(List<string> events) : IRelayBankController
    {
        public Task<RelayAction> StopAsync(CancellationToken cancellationToken) => Task.FromResult(RelayAction.StopSent);
        public Task<RelayAction> ResetAsync(CancellationToken cancellationToken) => Task.FromResult(RelayAction.ResetSent);
        public Task<RelayAction> TestStopAsync(CancellationToken cancellationToken) => Task.FromResult(RelayAction.TestStopSent);

        public Task OpenAllInterlocksAsync(CancellationToken cancellationToken)
        {
            events.Add("physical-open-all");
            return Task.CompletedTask;
        }

        public Task CloseAllInterlocksAsync(CancellationToken cancellationToken)
        {
            events.Add("physical-close-all");
            return Task.CompletedTask;
        }

        public Task SetChannelClosedAsync(int channelNumber, bool closed, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
