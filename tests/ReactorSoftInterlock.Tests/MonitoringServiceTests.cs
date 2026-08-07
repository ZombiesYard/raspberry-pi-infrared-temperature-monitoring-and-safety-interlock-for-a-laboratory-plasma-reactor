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
    public async Task RunLoopRetriesStopAfterTheFirstOutputAttemptFails()
    {
        var reader = new QueueTemperatureReader(
            new TemperatureReading(91.0, "Max 91.0 C", "roi"),
            new TemperatureReading(84.0, "Max 84.0 C", "roi"));
        var relay = new FailFirstStopRelay();
        var log = new InMemorySampleLog();
        var service = new MonitoringService(
            reader,
            relay,
            log,
            new FixedClock(),
            new InterlockStateMachine(new InterlockSettings(90.0)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        service.SampleRecorded += (_, _) =>
        {
            if (log.Samples.Count == 2)
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RunAsync(TimeSpan.FromMilliseconds(1), cancellation.Token));

        Assert.Equal(2, relay.StopCount);
        Assert.Equal(RelayAction.Failed, log.Samples[0].RelayAction);
        Assert.Equal(MonitorStatus.RelayTestFailed, log.Samples[0].Status);
        Assert.Equal(RelayAction.StopSent, log.Samples[1].RelayAction);
        Assert.Equal(MonitorStatus.Tripped, log.Samples[1].Status);
    }

    [Fact]
    public async Task RunLoopRetriesStopAfterTheFirstOutputReturnsUnexpectedAction()
    {
        var reader = new QueueTemperatureReader(
            new TemperatureReading(91.0, "Max 91.0 C", "roi"),
            new TemperatureReading(84.0, "Max 84.0 C", "roi"));
        var relay = new UnexpectedFirstStopRelay();
        var log = new InMemorySampleLog();
        var service = new MonitoringService(
            reader,
            relay,
            log,
            new FixedClock(),
            new InterlockStateMachine(new InterlockSettings(90.0)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        service.SampleRecorded += (_, _) =>
        {
            if (log.Samples.Count == 2)
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RunAsync(TimeSpan.FromMilliseconds(1), cancellation.Token));

        Assert.Equal(2, relay.StopCount);
        Assert.Equal(RelayAction.Failed, log.Samples[0].RelayAction);
        Assert.Equal(MonitorStatus.RelayTestFailed, log.Samples[0].Status);
        Assert.Equal(RelayAction.StopSent, log.Samples[1].RelayAction);
        Assert.Equal(MonitorStatus.Tripped, log.Samples[1].Status);
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
    public async Task AutoResetUsesNewStablePeriodAfterLatchedMachineIsReconfigured()
    {
        var original = new InterlockStateMachine(new InterlockSettings(90.0));
        Assert.True(original.Evaluate(95.0).ShouldSendStop);
        original.ConfirmStopSent();
        var reconfigured = original.Reconfigure(new InterlockSettings(190.0));
        var reader = new QueueTemperatureReader(
            new TemperatureReading(100.0, "Max 100.0 C", "roi"),
            new TemperatureReading(100.0, "Max 100.0 C", "roi"));
        var relay = new CountingRelay();
        var log = new InMemorySampleLog();
        var clock = new MutableClock(DateTimeOffset.Parse("2026-08-07T14:30:00+02:00"));
        var service = new MonitoringService(
            reader,
            relay,
            log,
            clock,
            reconfigured,
            new AutoResetOptions(true, 160.0, 3));

        await service.PollOnceAsync(CancellationToken.None);
        Assert.Equal(0, relay.ResetCount);

        clock.Advance(TimeSpan.FromSeconds(3));
        await service.PollOnceAsync(CancellationToken.None);

        Assert.Equal(0, relay.StopCount);
        Assert.Equal(1, relay.ResetCount);
        Assert.False(reconfigured.IsTripped);
        Assert.Equal(RelayAction.ResetSent, log.Samples.Last().RelayAction);
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

    private sealed class FailFirstStopRelay : IRelayController
    {
        public int StopCount { get; private set; }

        public Task<RelayAction> StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            return StopCount == 1
                ? Task.FromException<RelayAction>(new InvalidOperationException("Simulated relay failure."))
                : Task.FromResult(RelayAction.StopSent);
        }

        public Task<RelayAction> ResetAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(RelayAction.ResetSent);
        }

        public Task<RelayAction> TestStopAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(RelayAction.TestStopSent);
        }
    }

    private sealed class UnexpectedFirstStopRelay : IRelayController
    {
        public int StopCount { get; private set; }

        public Task<RelayAction> StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            return Task.FromResult(StopCount == 1 ? RelayAction.None : RelayAction.StopSent);
        }

        public Task<RelayAction> ResetAsync(CancellationToken cancellationToken)
        {
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

        public Task ClearAsync(CancellationToken cancellationToken)
        {
            Samples.Clear();
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
