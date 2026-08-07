using System.Diagnostics;
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
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly object _scheduleSync = new();
    private AutoResetOptions _autoResetOptions;
    private DateTimeOffset? _recoveryStableSince;
    private bool _hasTemperatureObservation;
    private double? _lastObservedTemperatureC;
    private TimeSpan _pollInterval = TimeSpan.FromSeconds(1);
    private bool _immediatePollRequested;
    private TaskCompletionSource _pollIntervalChanged = CreateScheduleSignal();

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
        await _pollGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        TemperatureSample sample;
        try
        {
            var reading = await _temperatureReader.ReadAsync(cancellationToken).ConfigureAwait(false);
            _hasTemperatureObservation = true;
            _lastObservedTemperatureC = reading.TemperatureC;
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

            sample = new TemperatureSample(
                _clock.Now,
                reading.TemperatureC,
                reading.RawText,
                status,
                alarmReason,
                relayAction,
                reading.RoiDescription);

            await _sampleLog.AppendAsync(sample, cancellationToken).ConfigureAwait(false);
            MarkConfigurationPolled();
        }
        finally
        {
            _pollGate.Release();
        }

        SampleRecorded?.Invoke(this, sample);
        return sample;
    }

    public async Task ApplyRuntimeConfigurationAsync(
        InterlockSettings interlockSettings,
        AutoResetOptions autoResetOptions,
        TimeSpan pollInterval,
        Action activateExternalConfiguration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(interlockSettings);
        ArgumentNullException.ThrowIfNull(autoResetOptions);
        ArgumentNullException.ThrowIfNull(activateExternalConfiguration);
        ValidatePollInterval(pollInterval);

        await _pollGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            activateExternalConfiguration();
            _stateMachine.ApplySettings(interlockSettings);
            _autoResetOptions = autoResetOptions;
            _recoveryStableSince = null;
            SetPollInterval(pollInterval, wakeImmediately: true);
        }
        finally
        {
            _pollGate.Release();
        }
    }

    public async Task<RelayAction?> TryManualResetAsync(
        double? fallbackTemperatureC,
        CancellationToken cancellationToken)
    {
        await _pollGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var currentTemperatureC = _hasTemperatureObservation
                ? _lastObservedTemperatureC
                : fallbackTemperatureC;
            if (!_stateMachine.CanReset(currentTemperatureC))
            {
                return null;
            }

            try
            {
                var relayAction = await _relayController.ResetAsync(cancellationToken).ConfigureAwait(false);
                _stateMachine.Reset(currentTemperatureC);
                _recoveryStableSince = null;
                return relayAction;
            }
            catch
            {
                await ReassertStopAfterFailedResetAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private async Task ReassertStopAfterFailedResetAsync()
    {
        if (!_stateMachine.IsTripped)
        {
            return;
        }

        _stateMachine.RequireStopConfirmation();
        try
        {
            var relayAction = await _relayController.StopAsync(CancellationToken.None).ConfigureAwait(false);
            if (relayAction == RelayAction.StopSent)
            {
                _stateMachine.ConfirmStopSent();
            }
        }
        catch
        {
            // The trip remains unconfirmed so the next poll retries the safety stop.
        }
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
        ValidatePollInterval(interval);
        SetPollInterval(interval, wakeImmediately: false);
        var nextTick = Stopwatch.GetTimestamp();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var schedule = GetPollingSchedule();
            var intervalTicks = Math.Max(1L, (long)(schedule.Interval.TotalSeconds * Stopwatch.Frequency));
            var scheduleChanged = false;

            if (!schedule.ImmediatePollRequested)
            {
                nextTick += intervalTicks;
                while (true)
                {
                    var remainingTicks = nextTick - Stopwatch.GetTimestamp();
                    if (remainingTicks <= 0)
                    {
                        break;
                    }

                    var delay = Task.Delay(
                        TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency),
                        cancellationToken);
                    var completed = await Task.WhenAny(delay, schedule.Changed).ConfigureAwait(false);
                    if (completed == schedule.Changed)
                    {
                        scheduleChanged = true;
                        break;
                    }

                    await delay.ConfigureAwait(false);
                    break;
                }
            }

            if (scheduleChanged)
            {
                nextTick = Stopwatch.GetTimestamp();
                continue;
            }

            await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            if (schedule.Changed.IsCompleted)
            {
                nextTick = Stopwatch.GetTimestamp();
            }
            else if (nextTick < Stopwatch.GetTimestamp() - intervalTicks)
            {
                nextTick = Stopwatch.GetTimestamp();
            }
        }
    }

    private PollingSchedule GetPollingSchedule()
    {
        lock (_scheduleSync)
        {
            return new PollingSchedule(_pollInterval, _pollIntervalChanged.Task, _immediatePollRequested);
        }
    }

    private void SetPollInterval(TimeSpan interval, bool wakeImmediately)
    {
        ValidatePollInterval(interval);
        TaskCompletionSource? previousSignal = null;
        lock (_scheduleSync)
        {
            if (_pollInterval == interval && !wakeImmediately)
            {
                return;
            }

            _pollInterval = interval;
            _immediatePollRequested |= wakeImmediately;
            previousSignal = _pollIntervalChanged;
            _pollIntervalChanged = CreateScheduleSignal();
        }

        previousSignal.TrySetResult();
    }

    private void MarkConfigurationPolled()
    {
        lock (_scheduleSync)
        {
            _immediatePollRequested = false;
        }
    }

    private static void ValidatePollInterval(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "Poll interval must be positive.");
        }
    }

    private static TaskCompletionSource CreateScheduleSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly record struct PollingSchedule(
        TimeSpan Interval,
        Task Changed,
        bool ImmediatePollRequested);
}
