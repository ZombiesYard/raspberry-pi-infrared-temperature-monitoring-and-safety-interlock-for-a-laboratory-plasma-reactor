using ReactorSoftInterlock.Application.G2000;
using ReactorSoftInterlock.Application.Ports;
using ReactorSoftInterlock.Domain;

namespace ReactorSoftInterlock.Infrastructure.Relay;

public sealed class HybridG2000InterlockController : IG2000Controller
{
    private readonly IG2000Controller _g2000;
    private readonly IRelayBankController _physicalInterlock;

    public HybridG2000InterlockController(IG2000Controller g2000, IRelayBankController physicalInterlock)
    {
        _g2000 = g2000;
        _physicalInterlock = physicalInterlock;
        _g2000.TelemetryUpdated += ForwardTelemetryUpdated;
    }

    public event EventHandler<G2000TelemetrySnapshot>? TelemetryUpdated;

    public G2000TelemetrySnapshot Snapshot => _g2000.Snapshot;

    public G2000WritableSetpoints TargetSetpoints => _g2000.TargetSetpoints;

    public G2000StartupRecipe StartupRecipe => _g2000.StartupRecipe;

    public TripRecoveryPolicy RecoveryPolicy
    {
        get => _g2000.RecoveryPolicy;
        set => _g2000.RecoveryPolicy = value;
    }

    public G2000UiMode UiMode
    {
        get => _g2000.UiMode;
        set => _g2000.UiMode = value;
    }

    public bool IsTripLatched => _g2000.IsTripLatched;

    public async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        await _g2000.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RelayAction> StopAsync(CancellationToken cancellationToken)
    {
        await _g2000.StopAsync(cancellationToken).ConfigureAwait(false);
        await _physicalInterlock.OpenAllInterlocksAsync(cancellationToken).ConfigureAwait(false);
        return RelayAction.StopSent;
    }

    public async Task<RelayAction> ResetAsync(CancellationToken cancellationToken)
    {
        await _physicalInterlock.CloseAllInterlocksAsync(cancellationToken).ConfigureAwait(false);
        return await _g2000.ResetAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RelayAction> TestStopAsync(CancellationToken cancellationToken)
    {
        await _physicalInterlock.OpenAllInterlocksAsync(cancellationToken).ConfigureAwait(false);
        await _g2000.SetHvStateAsync(G2000HvState.HvAus, cancellationToken).ConfigureAwait(false);
        return RelayAction.TestStopSent;
    }

    public Task OpenAllInterlocksAsync(CancellationToken cancellationToken)
    {
        return _physicalInterlock.OpenAllInterlocksAsync(cancellationToken);
    }

    public Task CloseAllInterlocksAsync(CancellationToken cancellationToken)
    {
        return _physicalInterlock.CloseAllInterlocksAsync(cancellationToken);
    }

    public Task SetChannelClosedAsync(int channelNumber, bool closed, CancellationToken cancellationToken)
    {
        return _physicalInterlock.SetChannelClosedAsync(channelNumber, closed, cancellationToken);
    }

    public Task SetHvStateAsync(G2000HvState state, CancellationToken cancellationToken)
    {
        return _g2000.SetHvStateAsync(state, cancellationToken);
    }

    public Task ApplyWritableSetpointsAsync(G2000WritableSetpoints setpoints, CancellationToken cancellationToken)
    {
        return _g2000.ApplyWritableSetpointsAsync(setpoints, cancellationToken);
    }

    public Task StartAutomaticSequenceAsync(G2000StartupRecipe recipe, CancellationToken cancellationToken)
    {
        return _g2000.StartAutomaticSequenceAsync(recipe, cancellationToken);
    }

    public Task StopAutomaticSequenceAsync(CancellationToken cancellationToken)
    {
        return _g2000.StopAutomaticSequenceAsync(cancellationToken);
    }

    public void Dispose()
    {
        _g2000.TelemetryUpdated -= ForwardTelemetryUpdated;
        (_g2000 as IDisposable)?.Dispose();
        (_physicalInterlock as IDisposable)?.Dispose();
    }

    private void ForwardTelemetryUpdated(object? sender, G2000TelemetrySnapshot snapshot)
    {
        TelemetryUpdated?.Invoke(this, snapshot);
    }
}
