using ReactorSoftInterlock.Application;
using ReactorSoftInterlock.Application.Ports;
using ReactorSoftInterlock.Domain;

namespace ReactorSoftInterlock.Tests;

public sealed class MonitoringServiceTests
{
    [Fact]
    public async Task SendsStopOnlyForFirstOverThresholdSample()
    {
        var reader = new QueueTemperatureReader(
            new TemperatureReading(89.9, "Max 89.9 C", "roi"),
            new TemperatureReading(90.0, "Max 90.0 C", "roi"),
            new TemperatureReading(91.0, "Max 91.0 C", "roi"));
        var relay = new CountingRelay();
        var log = new InMemorySampleLog();
        var service = new MonitoringService(
            reader,
            relay,
            log,
            new FixedClock(),
            new InterlockStateMachine(new InterlockSettings(90.0)));

        await service.PollOnceAsync(CancellationToken.None);
        await service.PollOnceAsync(CancellationToken.None);
        await service.PollOnceAsync(CancellationToken.None);

        Assert.Equal(1, relay.StopCount);
        Assert.Equal(MonitorStatus.Tripped, log.Samples.Last().Status);
    }

    [Fact]
    public async Task AutoResetWaitsForStableRecoveryPeriod()
    {
        var reader = new QueueTemperatureReader(
            new TemperatureReading(91.0, "Max 91.0 C", "roi"),
            new TemperatureReading(84.0, "Max 84.0 C", "roi"),
            new TemperatureReading(84.0, "Max 84.0 C", "roi"));
        var relay = new CountingRelay();
        var log = new InMemorySampleLog();
        var clock = new MutableClock(DateTimeOffset.Parse("2026-04-14T12:00:00+02:00"));
        var service = CreateService(reader, relay, log, clock, new AutoResetOptions(true, 85.0, 30));

        await service.PollOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(10));
        await service.PollOnceAsync(CancellationToken.None);
        Assert.Equal(0, relay.ResetCount);

        clock.Advance(TimeSpan.FromSeconds(30));
        await service.PollOnceAsync(CancellationToken.None);

        Assert.Equal(1, relay.StopCount);
        Assert.Equal(1, relay.ResetCount);
        Assert.Equal(RelayAction.ResetSent, log.Samples.Last().RelayAction);
        Assert.Equal(MonitorStatus.Monitoring, log.Samples.Last().Status);
    }

    [Fact]
    public async Task AutoResetRecoveryTimerIsClearedByNoReading()
    {
        var reader = new QueueTemperatureReader(
            new TemperatureReading(91.0, "Max 91.0 C", "roi"),
            new TemperatureReading(84.0, "Max 84.0 C", "roi"),
            new TemperatureReading(null, "NO READING", "roi"),
            new TemperatureReading(84.0, "Max 84.0 C", "roi"),
            new TemperatureReading(84.0, "Max 84.0 C", "roi"));
        var relay = new CountingRelay();
        var log = new InMemorySampleLog();
        var clock = new MutableClock(DateTimeOffset.Parse("2026-04-14T12:00:00+02:00"));
        var service = CreateService(reader, relay, log, clock, new AutoResetOptions(true, 85.0, 30));

        await service.PollOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(5));
        await service.PollOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(40));
        await service.PollOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        await service.PollOnceAsync(CancellationToken.None);
        Assert.Equal(0, relay.ResetCount);

        clock.Advance(TimeSpan.FromSeconds(30));
        await service.PollOnceAsync(CancellationToken.None);

        Assert.Equal(1, relay.ResetCount);
    }

    [Fact]
    public async Task AutoResetCanBeDisabled()
    {
        var reader = new QueueTemperatureReader(
            new TemperatureReading(91.0, "Max 91.0 C", "roi"),
            new TemperatureReading(84.0, "Max 84.0 C", "roi"),
            new TemperatureReading(84.0, "Max 84.0 C", "roi"));
        var relay = new CountingRelay();
        var log = new InMemorySampleLog();
        var clock = new MutableClock(DateTimeOffset.Parse("2026-04-14T12:00:00+02:00"));
        var service = CreateService(reader, relay, log, clock, new AutoResetOptions(false, 85.0, 30));

        await service.PollOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(60));
        await service.PollOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(60));
        await service.PollOnceAsync(CancellationToken.None);

        Assert.Equal(1, relay.StopCount);
        Assert.Equal(0, relay.ResetCount);
        Assert.Equal(MonitorStatus.Tripped, log.Samples.Last().Status);
    }

    [Fact]
    public async Task TripsAgainAfterAutoReset()
    {
        var reader = new QueueTemperatureReader(
            new TemperatureReading(91.0, "Max 91.0 C", "roi"),
            new TemperatureReading(84.0, "Max 84.0 C", "roi"),
            new TemperatureReading(84.0, "Max 84.0 C", "roi"),
            new TemperatureReading(91.0, "Max 91.0 C", "roi"));
        var relay = new CountingRelay();
        var log = new InMemorySampleLog();
        var clock = new MutableClock(DateTimeOffset.Parse("2026-04-14T12:00:00+02:00"));
        var service = CreateService(reader, relay, log, clock, new AutoResetOptions(true, 85.0, 30));

        await service.PollOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        await service.PollOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(30));
        await service.PollOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        await service.PollOnceAsync(CancellationToken.None);

        Assert.Equal(2, relay.StopCount);
        Assert.Equal(1, relay.ResetCount);
        Assert.Equal(MonitorStatus.Tripped, log.Samples.Last().Status);
    }

    private static MonitoringService CreateService(
        ITemperatureReader reader,
        IRelayController relay,
        ISampleLog log,
        IClock clock,
        AutoResetOptions options)
    {
        return new MonitoringService(
            reader,
            relay,
            log,
            clock,
            new InterlockStateMachine(new InterlockSettings(90.0)),
            options);
    }

    private sealed class QueueTemperatureReader : ITemperatureReader
    {
        private readonly Queue<TemperatureReading> _readings;

        public QueueTemperatureReader(params TemperatureReading[] readings)
        {
            _readings = new Queue<TemperatureReading>(readings);
        }

        public Task<TemperatureReading> ReadAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(_readings.Dequeue());
        }
    }

    private sealed class CountingRelay : IRelayController
    {
        public int StopCount { get; private set; }

        public int ResetCount { get; private set; }

        public Task<RelayAction> StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            return Task.FromResult(RelayAction.StopSent);
        }

        public Task<RelayAction> ResetAsync(CancellationToken cancellationToken)
        {
            ResetCount++;
            return Task.FromResult(RelayAction.ResetSent);
        }

        public Task<RelayAction> TestStopAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(RelayAction.TestStopSent);
        }
    }

    private sealed class InMemorySampleLog : ISampleLog
    {
        public List<TemperatureSample> Samples { get; } = [];

        public Task AppendAsync(TemperatureSample sample, CancellationToken cancellationToken)
        {
            Samples.Add(sample);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TemperatureSample>> ReadRecentAsync(int maxRows, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<TemperatureSample>>(Samples.TakeLast(maxRows).ToList());
        }

        public Task ExportAsync(string destinationPath, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now => DateTimeOffset.Parse("2026-04-14T12:00:00+02:00");
    }

    private sealed class MutableClock : IClock
    {
        public MutableClock(DateTimeOffset now)
        {
            Now = now;
        }

        public DateTimeOffset Now { get; private set; }

        public void Advance(TimeSpan value)
        {
            Now = Now.Add(value);
        }
    }
}
