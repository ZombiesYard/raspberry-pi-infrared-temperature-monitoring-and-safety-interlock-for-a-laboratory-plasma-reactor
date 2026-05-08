namespace ReactorSoftInterlock.Application.Ports;

public interface IGasFlowController
{
    Task StopFlowAsync(CancellationToken cancellationToken);

    Task RestoreFlowAsync(CancellationToken cancellationToken);

    Task SetTargetFlowAsync(double targetFlowMlMin, CancellationToken cancellationToken);

    Task<double?> ReadActualFlowAsync(CancellationToken cancellationToken);
}
