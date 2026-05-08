using ReactorSoftInterlock.Application.G2000;

namespace ReactorSoftInterlock.Application.Ports;

public interface IG2000Controller : IRelayBankController, IDisposable
{
    event EventHandler<G2000TelemetrySnapshot>? TelemetryUpdated;

    G2000TelemetrySnapshot Snapshot { get; }

    G2000WritableSetpoints TargetSetpoints { get; }

    G2000StartupRecipe StartupRecipe { get; }

    TripRecoveryPolicy RecoveryPolicy { get; set; }

    G2000UiMode UiMode { get; set; }

    bool IsTripLatched { get; }

    Task EnsureConnectedAsync(CancellationToken cancellationToken);

    Task SetHvStateAsync(G2000HvState state, CancellationToken cancellationToken);

    Task ApplyWritableSetpointsAsync(G2000WritableSetpoints setpoints, CancellationToken cancellationToken);

    Task StartAutomaticSequenceAsync(G2000StartupRecipe recipe, CancellationToken cancellationToken);

    Task StopAutomaticSequenceAsync(CancellationToken cancellationToken);
}
