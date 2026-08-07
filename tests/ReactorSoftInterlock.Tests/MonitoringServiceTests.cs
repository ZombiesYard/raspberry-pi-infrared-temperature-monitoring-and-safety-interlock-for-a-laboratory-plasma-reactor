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
    public async Task ApplyRuntimeConfigurationUsesNewThresholdWithoutReplacingService()
    {
        var reader = new QueueTemperatureReader(
            new TemperatureReading(100.0, "Max 100.0 C", "roi"),
            new TemperatureReading(100.0, "Max 100.0 C", "roi"));
        var relay = new CountingRelay();
        var log = new InMemorySampleLog();
        var machine = new InterlockStateMachine(new InterlockSettings(190.0));
        var service = new MonitoringService(reader, relay, log, new FixedClock(), machine);
        var externalConfigurationApplied = 0;

        var before = await service.PollOnceAsync(CancellationToken.None);
        await service.ApplyRuntimeConfigurationAsync(
            new InterlockSettings(90.0),
            new AutoResetOptions(false),
            TimeSpan.FromSeconds(1),
            () => externalConfigurationApplied++,
            CancellationToken.None);
        var after = await service.PollOnceAsync(CancellationToken.None);

        Assert.Equal(MonitorStatus.Monitoring, before.Status);
        Assert.Equal(MonitorStatus.Tripped, after.Status);
        Assert.Equal(RelayAction.StopSent, after.RelayAction);
        Assert.Equal(1, relay.StopCount);
        Assert.Equal(1, externalConfigurationApplied);
        Assert.True(machine.IsTripped);
    }

    [Fact]
    public async Task ApplyRuntimeConfigurationUpdatesRecoveryPolicyForLatchedTrip()
    {
        var machine = new InterlockStateMachine(new InterlockSettings(90.0));
        Assert.True(machine.Evaluate(95.0).ShouldSendStop);
        machine.ConfirmStopSent();
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
            machine,
            new AutoResetOptions(false));

        await service.ApplyRuntimeConfigurationAsync(
            new InterlockSettings(190.0),
            new AutoResetOptions(true, 160.0, 3),
            TimeSpan.FromSeconds(1),
            static () => { },
            CancellationToken.None);
        await service.PollOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(3));
        await service.PollOnceAsync(CancellationToken.None);

        Assert.Equal(1, relay.ResetCount);
        Assert.False(machine.IsTripped);
        Assert.Equal(RelayAction.ResetSent, log.Samples.Last().RelayAction);
    }

    [Fact]
    public async Task ApplyRuntimeConfigurationWaitsForInFlightSampleBoundary()
    {
        var reader = new BlockingTemperatureReader(new TemperatureReading(20.0, "Max 20.0 C", "roi"));
        var service = new MonitoringService(
            reader,
            new CountingRelay(),
            new InMemorySampleLog(),
            new FixedClock(),
            new InterlockStateMachine(new InterlockSettings(90.0)));
        var pollTask = service.PollOnceAsync(CancellationToken.None);
        await reader.Started;
        var externalConfigurationApplied = false;

        var applyTask = service.ApplyRuntimeConfigurationAsync(
            new InterlockSettings(100.0),
            new AutoResetOptions(false),
            TimeSpan.FromSeconds(1),
            () => externalConfigurationApplied = true,
            CancellationToken.None);
        await Task.Delay(25);

        Assert.False(externalConfigurationApplied);
        reader.Release();
        await pollTask;
        await applyTask;
        Assert.True(externalConfigurationApplied);
    }

    [Fact]
    public async Task ApplyRuntimeConfigurationImmediatelyPollsWhenIntervalIsUnchanged()
    {
        var reader = new ConstantTemperatureReader(new TemperatureReading(100.0, "Max 100.0 C", "roi"));
        var relay = new CountingRelay();
        var log = new InMemorySampleLog();
        var service = new MonitoringService(
            reader,
            relay,
            log,
            new FixedClock(),
            new InterlockStateMachine(new InterlockSettings(190.0)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var sampleRecorded = new TaskCompletionSource<TemperatureSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SampleRecorded += (_, sample) =>
        {
            sampleRecorded.TrySetResult(sample);
            cancellation.Cancel();
        };
        var runTask = service.RunAsync(TimeSpan.FromSeconds(30), cancellation.Token);
        await Task.Delay(25);

        await service.ApplyRuntimeConfigurationAsync(
            new InterlockSettings(90.0),
            new AutoResetOptions(false),
            TimeSpan.FromSeconds(30),
            static () => { },
            CancellationToken.None);

        var completed = await Task.WhenAny(sampleRecorded.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Same(sampleRecorded.Task, completed);
        var sample = await sampleRecorded.Task;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        Assert.Equal(MonitorStatus.Tripped, sample.Status);
        Assert.Equal(1, relay.StopCount);
        Assert.Single(log.Samples);
    }

    [Fact]
    public async Task ApplyBeforeRunPersistsImmediatePollRequest()
    {
        var reader = new ConstantTemperatureReader(new TemperatureReading(100.0, "Max 100.0 C", "roi"));
        var relay = new CountingRelay();
        var service = new MonitoringService(
            reader,
            relay,
            new InMemorySampleLog(),
            new FixedClock(),
            new InterlockStateMachine(new InterlockSettings(190.0)));
        await service.ApplyRuntimeConfigurationAsync(
            new InterlockSettings(90.0),
            new AutoResetOptions(false),
            TimeSpan.FromSeconds(30),
            static () => { },
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var sampleRecorded = new TaskCompletionSource<TemperatureSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SampleRecorded += (_, sample) =>
        {
            sampleRecorded.TrySetResult(sample);
            cancellation.Cancel();
        };

        var runTask = service.RunAsync(TimeSpan.FromSeconds(30), cancellation.Token);

        var completed = await Task.WhenAny(sampleRecorded.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Same(sampleRecorded.Task, completed);
        Assert.Equal(MonitorStatus.Tripped, (await sampleRecorded.Task).Status);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        Assert.Equal(1, relay.StopCount);
    }

    [Fact]
    public async Task DueTickUsingNewConfigurationDoesNotAddDuplicateImmediatePoll()
    {
        var log = new InMemorySampleLog();
        var service = new MonitoringService(
            new ConstantTemperatureReader(new TemperatureReading(20.0, "Max 20.0 C", "roi")),
            new CountingRelay(),
            log,
            new FixedClock(),
            new InterlockStateMachine(new InterlockSettings(90.0)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var firstSample = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SampleRecorded += (_, _) => firstSample.TrySetResult();
        var runTask = service.RunAsync(TimeSpan.FromMilliseconds(200), cancellation.Token);
        await Task.Delay(25);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCallback = new ManualResetEventSlim();

        var applyTask = Task.Run(() => service.ApplyRuntimeConfigurationAsync(
            new InterlockSettings(100.0),
            new AutoResetOptions(false),
            TimeSpan.FromMilliseconds(200),
            () =>
            {
                callbackEntered.TrySetResult();
                releaseCallback.Wait();
            },
            CancellationToken.None));
        await callbackEntered.Task;
        await Task.Delay(250);
        releaseCallback.Set();
        await applyTask;
        await firstSample.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(50);

        Assert.Single(log.Samples);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task ManualResetAndRuntimeConfigurationAreSerializedAtSampleBoundary()
    {
        var machine = new InterlockStateMachine(new InterlockSettings(90.0));
        Assert.True(machine.Evaluate(95.0).ShouldSendStop);
        machine.ConfirmStopSent();
        var relay = new BlockingResetRelay();
        var service = new MonitoringService(
            new ConstantTemperatureReader(new TemperatureReading(80.0, "Max 80.0 C", "roi")),
            relay,
            new InMemorySampleLog(),
            new FixedClock(),
            machine);
        var resetTask = service.TryManualResetAsync(80.0, CancellationToken.None);
        await relay.ResetStarted;
        var configurationApplied = false;

        var applyTask = service.ApplyRuntimeConfigurationAsync(
            new InterlockSettings(70.0),
            new AutoResetOptions(false),
            TimeSpan.FromSeconds(1),
            () => configurationApplied = true,
            CancellationToken.None);
        await Task.Delay(25);

        Assert.False(configurationApplied);
        relay.ReleaseReset();
        Assert.Equal(RelayAction.ResetSent, await resetTask);
        await applyTask;
        Assert.True(configurationApplied);
        Assert.False(machine.IsTripped);
    }

    [Fact]
    public async Task ManualResetFailureReassertsConfirmedStopBeforeReturning()
    {
        var machine = new InterlockStateMachine(new InterlockSettings(90.0));
        Assert.True(machine.Evaluate(95.0).ShouldSendStop);
        machine.ConfirmStopSent();
        var relay = new FailResetRelay();
        var service = new MonitoringService(
            new ConstantTemperatureReader(new TemperatureReading(80.0, "Max 80.0 C", "roi")),
            relay,
            new InMemorySampleLog(),
            new FixedClock(),
            machine);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.TryManualResetAsync(80.0, CancellationToken.None));

        Assert.True(machine.IsTripped);
        Assert.Equal(1, relay.ResetCount);
        Assert.Equal(1, relay.StopCount);
        Assert.False(machine.Evaluate(80.0).ShouldSendStop);
    }

    [Fact]
    public async Task ManualResetUsesLatestNoReadingInsteadOfStaleUiTemperature()
    {
        var machine = new InterlockStateMachine(new InterlockSettings(90.0));
        Assert.True(machine.Evaluate(95.0).ShouldSendStop);
        machine.ConfirmStopSent();
        var relay = new CountingRelay();
        var service = new MonitoringService(
            new QueueTemperatureReader(new TemperatureReading(null, "calibrating", "roi")),
            relay,
            new InMemorySampleLog(),
            new FixedClock(),
            machine);
        await service.PollOnceAsync(CancellationToken.None);

        var result = await service.TryManualResetAsync(20.0, CancellationToken.None);

        Assert.Null(result);
        Assert.True(machine.IsTripped);
        Assert.Equal(0, relay.ResetCount);
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

    private sealed class BlockingTemperatureReader : ITemperatureReader
    {
        private readonly TemperatureReading _reading;
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingTemperatureReader(TemperatureReading reading)
        {
            _reading = reading;
        }

        public Task Started => _started.Task;

        public void Release() => _release.TrySetResult();

        public async Task<TemperatureReading> ReadAsync(CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return _reading;
        }
    }

    private sealed class ConstantTemperatureReader : ITemperatureReader
    {
        private readonly TemperatureReading _reading;

        public ConstantTemperatureReader(TemperatureReading reading)
        {
            _reading = reading;
        }

        public Task<TemperatureReading> ReadAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(_reading);
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

    private sealed class FailResetRelay : IRelayController
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
            return Task.FromException<RelayAction>(new InvalidOperationException("Simulated reset failure."));
        }

        public Task<RelayAction> TestStopAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(RelayAction.TestStopSent);
        }
    }

    private sealed class BlockingResetRelay : IRelayController
    {
        private readonly TaskCompletionSource _resetStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseReset = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ResetStarted => _resetStarted.Task;

        public void ReleaseReset() => _releaseReset.TrySetResult();

        public Task<RelayAction> StopAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(RelayAction.StopSent);
        }

        public async Task<RelayAction> ResetAsync(CancellationToken cancellationToken)
        {
            _resetStarted.TrySetResult();
            await _releaseReset.Task.WaitAsync(cancellationToken);
            return RelayAction.ResetSent;
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
