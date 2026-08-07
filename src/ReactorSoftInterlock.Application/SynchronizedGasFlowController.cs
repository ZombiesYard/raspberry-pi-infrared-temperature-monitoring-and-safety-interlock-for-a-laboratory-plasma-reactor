using ReactorSoftInterlock.Application.Ports;

namespace ReactorSoftInterlock.Application;

public sealed class SynchronizedGasFlowController : IGasFlowController
{
    private readonly IGasFlowController _inner;
    private readonly SemaphoreSlim _gate;

    public SynchronizedGasFlowController(IGasFlowController inner, SemaphoreSlim gate)
    {
        _inner = inner;
        _gate = gate;
    }

    public Task StopFlowAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(() => _inner.StopFlowAsync(cancellationToken), cancellationToken);

    public Task RestoreFlowAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(() => _inner.RestoreFlowAsync(cancellationToken), cancellationToken);

    public Task SetTargetFlowAsync(double targetFlowMlMin, CancellationToken cancellationToken) =>
        ExecuteAsync(() => _inner.SetTargetFlowAsync(targetFlowMlMin, cancellationToken), cancellationToken);

    public Task<double?> ReadActualFlowAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(() => _inner.ReadActualFlowAsync(cancellationToken), cancellationToken);

    private async Task ExecuteAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> ExecuteAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
