using ReactorSoftInterlock.Application.Ports;
using ReactorSoftInterlock.Domain;

namespace ReactorSoftInterlock.Infrastructure.Relay;

public sealed class DryRunRelayController : IRelayBankController
{
    public Task<RelayAction> StopAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(RelayAction.StopSent);
    }

    public Task<RelayAction> ResetAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(RelayAction.ResetSent);
    }

    public Task<RelayAction> TestStopAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(RelayAction.TestStopSent);
    }

    public Task OpenAllInterlocksAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task CloseAllInterlocksAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task SetChannelClosedAsync(int channelNumber, bool closed, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
