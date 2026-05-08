using ReactorSoftInterlock.Application;
using ReactorSoftInterlock.Application.Ports;
using ReactorSoftInterlock.Domain;

namespace ReactorSoftInterlock.Tests;

public sealed class ProcessOutputControllerTests
{
    [Fact]
    public async Task StopAsyncDelegatesToRelayBank()
    {
        var events = new List<string>();
        var controller = new ProcessOutputController(new RecordingRelayBank(events));

        await controller.StopAsync(CancellationToken.None);

        Assert.Equal(["relay-stop"], events);
    }

    [Fact]
    public async Task ResetAsyncDelegatesToRelayBank()
    {
        var events = new List<string>();
        var controller = new ProcessOutputController(new RecordingRelayBank(events));

        await controller.ResetAsync(CancellationToken.None);

        Assert.Equal(["relay-reset"], events);
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
}
