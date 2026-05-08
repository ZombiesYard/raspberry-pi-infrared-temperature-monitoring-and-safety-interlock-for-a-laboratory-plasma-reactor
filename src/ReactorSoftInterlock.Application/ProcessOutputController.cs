using ReactorSoftInterlock.Application.Ports;
using ReactorSoftInterlock.Domain;

namespace ReactorSoftInterlock.Application;

public sealed class ProcessOutputController : IRelayBankController
{
    private readonly IRelayBankController _relayBankController;

    public ProcessOutputController(IRelayBankController relayBankController)
    {
        _relayBankController = relayBankController;
    }

    public Task<RelayAction> StopAsync(CancellationToken cancellationToken)
    {
        return _relayBankController.StopAsync(cancellationToken);
    }

    public Task<RelayAction> ResetAsync(CancellationToken cancellationToken)
    {
        return _relayBankController.ResetAsync(cancellationToken);
    }

    public Task<RelayAction> TestStopAsync(CancellationToken cancellationToken)
    {
        return _relayBankController.TestStopAsync(cancellationToken);
    }

    public Task OpenAllInterlocksAsync(CancellationToken cancellationToken)
    {
        return _relayBankController.OpenAllInterlocksAsync(cancellationToken);
    }

    public Task CloseAllInterlocksAsync(CancellationToken cancellationToken)
    {
        return _relayBankController.CloseAllInterlocksAsync(cancellationToken);
    }

    public Task SetChannelClosedAsync(int channelNumber, bool closed, CancellationToken cancellationToken)
    {
        return _relayBankController.SetChannelClosedAsync(channelNumber, closed, cancellationToken);
    }
}
