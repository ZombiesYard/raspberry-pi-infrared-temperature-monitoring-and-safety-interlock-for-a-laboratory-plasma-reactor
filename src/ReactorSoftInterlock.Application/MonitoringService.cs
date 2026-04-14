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

    public MonitoringService(
        ITemperatureReader temperatureReader,
        IRelayController relayController,
        ISampleLog sampleLog,
        IClock clock,
        InterlockStateMachine stateMachine)
    {
        _temperatureReader = temperatureReader;
        _relayController = relayController;
        _sampleLog = sampleLog;
        _clock = clock;
        _stateMachine = stateMachine;
    }

    public event EventHandler<TemperatureSample>? SampleRecorded;

    public async Task<TemperatureSample> PollOnceAsync(CancellationToken cancellationToken)
    {
        var reading = await _temperatureReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var decision = _stateMachine.Evaluate(reading.TemperatureC);
        var relayAction = decision.RelayAction;

        if (decision.ShouldSendStop)
        {
            relayAction = await _relayController.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        var sample = new TemperatureSample(
            _clock.Now,
            reading.TemperatureC,
            reading.RawText,
            decision.Status,
            decision.AlarmReason,
            relayAction,
            reading.RoiDescription);

        await _sampleLog.AppendAsync(sample, cancellationToken).ConfigureAwait(false);
        SampleRecorded?.Invoke(this, sample);
        return sample;
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
