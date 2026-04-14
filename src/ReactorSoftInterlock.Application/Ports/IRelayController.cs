using ReactorSoftInterlock.Domain;

namespace ReactorSoftInterlock.Application.Ports;

public interface IRelayController
{
    Task<RelayAction> StopAsync(CancellationToken cancellationToken);

    Task<RelayAction> ResetAsync(CancellationToken cancellationToken);

    Task<RelayAction> TestStopAsync(CancellationToken cancellationToken);
}
