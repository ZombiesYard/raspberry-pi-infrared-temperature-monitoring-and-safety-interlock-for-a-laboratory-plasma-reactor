namespace ReactorSoftInterlock.Domain;

public sealed class InterlockStateMachine
{
    private readonly InterlockSettings _settings;
    private bool _isTripped;
    private bool _stopAlreadySent;

    public InterlockStateMachine(InterlockSettings settings)
    {
        _settings = settings;
    }

    public bool IsTripped => _isTripped;

    public InterlockDecision Evaluate(double? temperatureC)
    {
        if (_isTripped)
        {
            return new InterlockDecision(
                MonitorStatus.Tripped,
                RelayAction.None,
                $"Latched trip: temperature reached or exceeded {_settings.ThresholdC:0.0} C.",
                ShouldSendStop: false);
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
            if (!_stopAlreadySent)
            {
                _stopAlreadySent = true;
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
        _stopAlreadySent = false;
    }
}
