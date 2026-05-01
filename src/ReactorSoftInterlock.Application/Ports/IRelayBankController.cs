namespace ReactorSoftInterlock.Application.Ports;

public interface IRelayBankController : IRelayController
{
    Task OpenAllInterlocksAsync(CancellationToken cancellationToken);

    Task CloseAllInterlocksAsync(CancellationToken cancellationToken);

    Task SetChannelClosedAsync(int channelNumber, bool closed, CancellationToken cancellationToken);
}
