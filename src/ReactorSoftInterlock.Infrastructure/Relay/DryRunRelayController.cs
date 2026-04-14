using ReactorSoftInterlock.Application.Ports;
using ReactorSoftInterlock.Domain;

namespace ReactorSoftInterlock.Infrastructure.Relay;

public sealed class DryRunRelayController : IRelayController
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
}
