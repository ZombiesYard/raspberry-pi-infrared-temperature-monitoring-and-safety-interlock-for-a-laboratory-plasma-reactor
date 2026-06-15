using ReactorSoftInterlock.Application.Ports;
using ReactorSoftInterlock.Domain;

namespace ReactorSoftInterlock.Application;

public sealed class SynchronizedRelayBankController : IRelayBankController
{
    private readonly IRelayBankController _inner;
    private readonly SemaphoreSlim _gate;

    public SynchronizedRelayBankController(IRelayBankController inner, SemaphoreSlim gate)
    {
        _inner = inner;
        _gate = gate;
    }

    public Task<RelayAction> StopAsync(CancellationToken cancellationToken)
    {
        return ExecuteAsync(() => _inner.StopAsync(cancellationToken), cancellationToken);
    }

    public Task<RelayAction> ResetAsync(CancellationToken cancellationToken)
    {
        return ExecuteAsync(() => _inner.ResetAsync(cancellationToken), cancellationToken);
    }

    public Task<RelayAction> TestStopAsync(CancellationToken cancellationToken)
    {
        return ExecuteAsync(() => _inner.TestStopAsync(cancellationToken), cancellationToken);
    }

    public Task OpenAllInterlocksAsync(CancellationToken cancellationToken)
    {
        return ExecuteAsync(() => _inner.OpenAllInterlocksAsync(cancellationToken), cancellationToken);
    }

    public Task CloseAllInterlocksAsync(CancellationToken cancellationToken)
    {
        return ExecuteAsync(() => _inner.CloseAllInterlocksAsync(cancellationToken), cancellationToken);
    }

    public Task SetChannelClosedAsync(int channelNumber, bool closed, CancellationToken cancellationToken)
    {
        return ExecuteAsync(() => _inner.SetChannelClosedAsync(channelNumber, closed, cancellationToken), cancellationToken);
    }

    private async Task ExecuteAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await action();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> ExecuteAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            _gate.Release();
        }
    }
}
