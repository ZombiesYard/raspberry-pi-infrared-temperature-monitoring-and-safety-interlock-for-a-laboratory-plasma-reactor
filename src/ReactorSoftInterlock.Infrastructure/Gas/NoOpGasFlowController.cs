using ReactorSoftInterlock.Application.Ports;

namespace ReactorSoftInterlock.Infrastructure.Gas;

public sealed class NoOpGasFlowController : IGasFlowController
{
    public Task StopFlowAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task RestoreFlowAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task SetTargetFlowAsync(double targetFlowMlMin, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task<double?> ReadActualFlowAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<double?>(null);
    }
}
