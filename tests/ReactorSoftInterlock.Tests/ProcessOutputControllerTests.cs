using ReactorSoftInterlock.Application;
using ReactorSoftInterlock.Application.Ports;
using ReactorSoftInterlock.Domain;

namespace ReactorSoftInterlock.Tests;

public sealed class ProcessOutputControllerTests
{
    [Fact]
    public async Task StopAsyncStopsGasBeforeOpeningInterlocks()
    {
        var events = new List<string>();
        var controller = new ProcessOutputController(new RecordingRelayBank(events), new RecordingGasController(events));

        await controller.StopAsync(CancellationToken.None);

        Assert.Equal(["gas-stop", "relay-stop"], events);
    }

    [Fact]
    public async Task ResetAsyncClosesInterlocksBeforeRestoringGas()
    {
        var events = new List<string>();
        var controller = new ProcessOutputController(new RecordingRelayBank(events), new RecordingGasController(events));

        await controller.ResetAsync(CancellationToken.None);

        Assert.Equal(["relay-reset", "gas-restore"], events);
    }

    private sealed class RecordingRelayBank(List<string> events) : IRelayBankController
    {
        public Task<RelayAction> StopAsync(CancellationToken cancellationToken)
        {
            events.Add("relay-stop");
            return Task.FromResult(RelayAction.StopSent);
        }

        public Task<RelayAction> ResetAsync(CancellationToken cancellationToken)
        {
            events.Add("relay-reset");
            return Task.FromResult(RelayAction.ResetSent);
        }

        public Task<RelayAction> TestStopAsync(CancellationToken cancellationToken)
        {
            events.Add("relay-test");
            return Task.FromResult(RelayAction.TestStopSent);
        }

        public Task OpenAllInterlocksAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CloseAllInterlocksAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetChannelClosedAsync(int channelNumber, bool closed, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingGasController(List<string> events) : IGasFlowController
    {
        public Task StopFlowAsync(CancellationToken cancellationToken)
        {
            events.Add("gas-stop");
            return Task.CompletedTask;
        }

        public Task RestoreFlowAsync(CancellationToken cancellationToken)
        {
            events.Add("gas-restore");
            return Task.CompletedTask;
        }

        public Task<double?> ReadActualFlowAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<double?>(123.4);
        }
    }
}
