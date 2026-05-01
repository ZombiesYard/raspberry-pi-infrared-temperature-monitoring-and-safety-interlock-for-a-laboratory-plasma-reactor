using ReactorSoftInterlock.Application.Ports;
using ReactorSoftInterlock.Domain;

namespace ReactorSoftInterlock.Application;

public sealed class ProcessOutputController : IRelayBankController
{
    private readonly IRelayBankController _relayBankController;
    private readonly IGasFlowController _gasFlowController;

    public ProcessOutputController(IRelayBankController relayBankController, IGasFlowController gasFlowController)
    {
        _relayBankController = relayBankController;
        _gasFlowController = gasFlowController;
    }

    public async Task<RelayAction> StopAsync(CancellationToken cancellationToken)
    {
        Exception? gasException = null;
        try
        {
            await _gasFlowController.StopFlowAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            gasException = ex;
        }

        var relayAction = await _relayBankController.StopAsync(cancellationToken).ConfigureAwait(false);
        if (gasException is not null)
        {
            throw new InvalidOperationException(
                "Interlocks were opened, but AMC2100 gas stop failed. Check the AMC2100 COM port, RS485 wiring, slave ID, and control mode.",
                gasException);
        }

        return relayAction;
    }

    public async Task<RelayAction> ResetAsync(CancellationToken cancellationToken)
    {
        var relayAction = await _relayBankController.ResetAsync(cancellationToken).ConfigureAwait(false);
        await _gasFlowController.RestoreFlowAsync(cancellationToken).ConfigureAwait(false);
        return relayAction;
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
