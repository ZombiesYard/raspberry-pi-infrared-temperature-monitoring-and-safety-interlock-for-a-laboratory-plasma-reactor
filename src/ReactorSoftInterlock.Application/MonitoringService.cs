using ReactorSoftInterlock.Application.Ports;
using ReactorSoftInterlock.Domain;

namespace ReactorSoftInterlock.Application;

public sealed class MonitoringService
{
    private readonly ITemperatureReader _temperatureReader;
    private readonly IRelayController _relayController;
    private readonly ISampleLog _sampleLog;
    private readonly IClock _clock;
    private readonly InterlockStateMachine _stateMachine;
    private readonly AutoResetOptions _autoResetOptions;
    private DateTimeOffset? _recoveryStableSince;

    public MonitoringService(
        ITemperatureReader temperatureReader,
        IRelayController relayController,
        ISampleLog sampleLog,
        IClock clock,
        InterlockStateMachine stateMachine,
        AutoResetOptions? autoResetOptions = null)
    {
        _temperatureReader = temperatureReader;
        _relayController = relayController;
        _sampleLog = sampleLog;
        _clock = clock;
        _stateMachine = stateMachine;
        _autoResetOptions = autoResetOptions ?? new AutoResetOptions(Enabled: false);
    }

    public event EventHandler<TemperatureSample>? SampleRecorded;

    public async Task<TemperatureSample> PollOnceAsync(CancellationToken cancellationToken)
    {
        var reading = await _temperatureReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var decision = _stateMachine.Evaluate(reading.TemperatureC);
        var relayAction = decision.RelayAction;
        var status = decision.Status;
        var alarmReason = decision.AlarmReason;

        if (decision.ShouldSendStop)
        {
            try
            {
                relayAction = await _relayController.StopAsync(cancellationToken).ConfigureAwait(false);
                if (relayAction != RelayAction.StopSent)
                {
                    throw new InvalidOperationException($"Interlock stop returned unexpected action {relayAction}.");
                }

                _stateMachine.ConfirmStopSent();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                relayAction = RelayAction.Failed;
                status = MonitorStatus.RelayTestFailed;
                alarmReason = $"Interlock stop failed and will be retried: {ex.Message}";
            }
        }

        if (_stateMachine.IsTripped && !decision.ShouldSendStop)
        {
            var autoResetDecision = await TryAutoResetAsync(reading.TemperatureC, cancellationToken).ConfigureAwait(false);
            if (autoResetDecision is not null)
            {
                status = autoResetDecision.Status;
                alarmReason = autoResetDecision.AlarmReason;
                relayAction = autoResetDecision.RelayAction;
            }
        }

        var sample = new TemperatureSample(
            _clock.Now,
            reading.TemperatureC,
            reading.RawText,
            status,
            alarmReason,
            relayAction,
            reading.RoiDescription);

        await _sampleLog.AppendAsync(sample, cancellationToken).ConfigureAwait(false);
        SampleRecorded?.Invoke(this, sample);
        return sample;
    }

    private async Task<InterlockDecision?> TryAutoResetAsync(double? temperatureC, CancellationToken cancellationToken)
    {
        if (!_autoResetOptions.Enabled)
        {
            _recoveryStableSince = null;
            return null;
        }

        if (temperatureC is null ||
            temperatureC.Value >= _autoResetOptions.RecoveryThresholdC ||
            !_stateMachine.CanReset(temperatureC))
        {
            _recoveryStableSince = null;
            return null;
        }

        var now = _clock.Now;
        _recoveryStableSince ??= now;
        var stableFor = now - _recoveryStableSince.Value;
        if (stableFor < TimeSpan.FromSeconds(_autoResetOptions.StableSeconds))
        {
            return null;
        }

        var relayAction = await _relayController.ResetAsync(cancellationToken).ConfigureAwait(false);
        _stateMachine.Reset(temperatureC);
        _recoveryStableSince = null;

        return new InterlockDecision(
            MonitorStatus.Monitoring,
            relayAction,
            $"Auto reset after temperature stayed below {_autoResetOptions.RecoveryThresholdC:0.0} C for {_autoResetOptions.StableSeconds} s.",
            ShouldSendStop: false);
    }

    public async Task RunAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await PollOnceAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
