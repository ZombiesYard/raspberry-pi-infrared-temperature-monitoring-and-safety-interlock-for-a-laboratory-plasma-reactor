namespace ReactorSoftInterlock.Domain;

public sealed class InterlockStateMachine
{
    private readonly InterlockSettings _settings;
    private bool _isTripped;
    private bool _stopConfirmed;
    private double? _tripThresholdC;

    public InterlockStateMachine(InterlockSettings settings)
    {
        _settings = settings;
    }

    public bool IsTripped => _isTripped;

    public InterlockDecision Evaluate(double? temperatureC)
    {
        if (_isTripped)
        {
            var tripThresholdC = _tripThresholdC ?? _settings.ThresholdC;
            var shouldSendStop = !_stopConfirmed;
            return new InterlockDecision(
                MonitorStatus.Tripped,
                shouldSendStop ? RelayAction.StopSent : RelayAction.None,
                $"Latched trip: temperature reached or exceeded {tripThresholdC:0.0} C.",
                ShouldSendStop: shouldSendStop);
        }

        if (temperatureC is null)
        {
            return new InterlockDecision(
                MonitorStatus.NoReading,
                RelayAction.None,
                "OCR did not return a valid temperature.",
                ShouldSendStop: false);
        }

        if (temperatureC.Value >= _settings.ThresholdC)
        {
            _isTripped = true;
            _tripThresholdC = _settings.ThresholdC;
            if (!_stopConfirmed)
            {
                return new InterlockDecision(
                    MonitorStatus.Tripped,
                    RelayAction.StopSent,
                    $"Temperature {temperatureC.Value:0.0} C reached or exceeded {_settings.ThresholdC:0.0} C.",
                    ShouldSendStop: true);
            }
        }

        return new InterlockDecision(
            MonitorStatus.Monitoring,
            RelayAction.None,
            string.Empty,
            ShouldSendStop: false);
    }

    public void ConfirmStopSent()
    {
        if (!_isTripped)
        {
            throw new InvalidOperationException("Cannot confirm a stop when no trip is latched.");
        }

        _stopConfirmed = true;
    }

    public void RequireStopConfirmation()
    {
        if (_isTripped)
        {
            _stopConfirmed = false;
        }
    }

    public InterlockStateMachine Reconfigure(InterlockSettings settings)
    {
        return new InterlockStateMachine(settings)
        {
            _isTripped = _isTripped,
            _stopConfirmed = _stopConfirmed,
            _tripThresholdC = _tripThresholdC
        };
    }

    public bool CanReset(double? currentTemperatureC)
    {
        return _isTripped && currentTemperatureC is not null && currentTemperatureC.Value < _settings.ThresholdC;
    }

    public void Reset(double? currentTemperatureC)
    {
        if (!CanReset(currentTemperatureC))
        {
            throw new InvalidOperationException("Cannot reset until a valid temperature is below the configured threshold.");
        }

        _isTripped = false;
        _stopConfirmed = false;
        _tripThresholdC = null;
    }
}
