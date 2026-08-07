using ReactorSoftInterlock.Application.G2000;
using ReactorSoftInterlock.Infrastructure.Relay;
using ReactorSoftInterlock.Infrastructure.Settings;

namespace ReactorSoftInterlock.Tests;

public sealed class G2000CanControllerAutomaticSequenceTests
{
    [Fact]
    public async Task AutomaticSequence_IsRejectedWhileHardwareFaultIsActive()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 8, 7, 14, 30, 0, TimeSpan.Zero));
        var bus = new FakePcanBus();
        using var controller = CreateController(bus, clock, hvReadyLeadTimeMs: 0);
        await controller.EnsureConnectedAsync(CancellationToken.None);
        await controller.HandleIncomingMessageForTestAsync(
            G2000CanProtocol.GetStatusId(0),
            [0x03, 0x00, 0x03, G2000CanProtocol.ErrorCodeInterlock, 0x00, 0x00, 0x00, 0x00],
            CancellationToken.None);

        var error = await Assert.ThrowsAsync<G2000AutomaticStartBlockedException>(() =>
            controller.StartAutomaticSequenceAsync(new G2000StartupRecipe(), CancellationToken.None));

        Assert.Contains("fault", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(G2000HvState.HvAus, controller.Snapshot.TargetHvState);
        Assert.NotEqual(G2000UiMode.Automatic, controller.Snapshot.UiMode);
    }

    [Fact]
    public async Task AutomaticSequence_StartsStage1TimerAfterHvReadyLead()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 5, 26, 12, 0, 0, TimeSpan.Zero));
        var bus = new FakePcanBus();
        using var controller = CreateController(bus, clock, hvReadyLeadTimeMs: 5000);
        var recipe = new G2000StartupRecipe
        {
            Stage1VoltageV = 63.0,
            Stage1DurationMs = 6000,
            Stage2VoltageV = 43.0,
            Stage2HoldEnabled = true,
            EnterHvReadyBeforeRun = true,
            EnterHvOnAtStart = true
        };

        await controller.StartAutomaticSequenceAsync(recipe, CancellationToken.None);
        Assert.Equal("ReadyLead", controller.Snapshot.AutomaticStage);
        Assert.Equal(G2000CanProtocol.CreateCanBusHvReadyData(), bus.LastCommandData());

        clock.Advance(TimeSpan.FromMilliseconds(5000));
        await controller.RunSenderTickForTestAsync(CancellationToken.None);
        Assert.Equal("Stage1", controller.Snapshot.AutomaticStage);
        Assert.Equal(63.0, controller.TargetSetpoints.VoltageV);
        Assert.Equal(G2000CanProtocol.CreateCanBusHvOnData(), bus.LastCommandData());

        clock.Advance(TimeSpan.FromMilliseconds(5999));
        await controller.RunSenderTickForTestAsync(CancellationToken.None);
        Assert.Equal("Stage1", controller.Snapshot.AutomaticStage);
        Assert.Equal(63.0, controller.TargetSetpoints.VoltageV);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        await controller.RunSenderTickForTestAsync(CancellationToken.None);
        Assert.Equal("Stage2", controller.Snapshot.AutomaticStage);
        Assert.Equal(43.0, controller.TargetSetpoints.VoltageV);
    }

    [Fact]
    public async Task StopAutomaticSequence_CancelsPendingHvOn()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 5, 26, 12, 0, 0, TimeSpan.Zero));
        var bus = new FakePcanBus();
        using var controller = CreateController(bus, clock, hvReadyLeadTimeMs: 5000);

        await controller.StartAutomaticSequenceAsync(new G2000StartupRecipe(), CancellationToken.None);
        await controller.StopAutomaticSequenceAsync(CancellationToken.None);

        Assert.Equal(G2000HvState.HvAus, controller.Snapshot.TargetHvState);
        Assert.Equal(G2000UiMode.Manual, controller.Snapshot.UiMode);
        Assert.Equal("Stopped", controller.Snapshot.AutomaticStage);
        Assert.Equal(G2000CanProtocol.CreateCanBusStopData(), bus.LastCommandData());

        clock.Advance(TimeSpan.FromMilliseconds(5000));
        await controller.RunSenderTickForTestAsync(CancellationToken.None);

        Assert.Equal(G2000HvState.HvAus, controller.Snapshot.TargetHvState);
        Assert.DoesNotContain(bus.CommandFramesAfter(4), data => data.SequenceEqual(G2000CanProtocol.CreateCanBusHvOnData()));
    }

    [Fact]
    public async Task StopAutomaticSequence_SendsHvAusWhenAlreadyOn()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 5, 26, 12, 0, 0, TimeSpan.Zero));
        var bus = new FakePcanBus();
        using var controller = CreateController(bus, clock, hvReadyLeadTimeMs: 0);
        var recipe = new G2000StartupRecipe
        {
            EnterHvReadyBeforeRun = false,
            EnterHvOnAtStart = true
        };

        await controller.StartAutomaticSequenceAsync(recipe, CancellationToken.None);
        Assert.Equal(G2000CanProtocol.CreateCanBusHvOnData(), bus.LastCommandData());

        await controller.StopAutomaticSequenceAsync(CancellationToken.None);

        Assert.Equal(G2000HvState.HvAus, controller.Snapshot.TargetHvState);
        Assert.Equal(G2000CanProtocol.CreateCanBusStopData(), bus.LastCommandData());
    }

    [Fact]
    public async Task RestorePreviousState_RerunsStartupRecipeFromStage1()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 5, 26, 12, 0, 0, TimeSpan.Zero));
        var bus = new FakePcanBus();
        using var controller = CreateController(bus, clock, hvReadyLeadTimeMs: 5000);
        controller.RecoveryPolicy = TripRecoveryPolicy.RestorePreviousState;
        var recipe = new G2000StartupRecipe
        {
            Stage1VoltageV = 62.0,
            Stage1DurationMs = 6000,
            Stage2VoltageV = 42.0,
            Stage2HoldEnabled = true,
            EnterHvReadyBeforeRun = true,
            EnterHvOnAtStart = true
        };

        await controller.StartAutomaticSequenceAsync(recipe, CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(5000));
        await controller.RunSenderTickForTestAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(6000));
        await controller.RunSenderTickForTestAsync(CancellationToken.None);
        Assert.Equal("Stage2", controller.Snapshot.AutomaticStage);
        Assert.Equal(42.0, controller.TargetSetpoints.VoltageV);

        await controller.StopAsync(CancellationToken.None);
        await controller.ResetAsync(CancellationToken.None);

        Assert.Equal("ReadyLead", controller.Snapshot.AutomaticStage);
        Assert.Equal(62.0, controller.TargetSetpoints.VoltageV);
        Assert.Equal(G2000CanProtocol.CreateCanBusHvReadyData(), bus.LastCommandData());

        clock.Advance(TimeSpan.FromMilliseconds(5000));
        await controller.RunSenderTickForTestAsync(CancellationToken.None);
        Assert.Equal("Stage1", controller.Snapshot.AutomaticStage);
        Assert.Equal(G2000CanProtocol.CreateCanBusHvOnData(), bus.LastCommandData());

        clock.Advance(TimeSpan.FromMilliseconds(6000));
        await controller.RunSenderTickForTestAsync(CancellationToken.None);
        Assert.Equal("Stage2", controller.Snapshot.AutomaticStage);
        Assert.Equal(42.0, controller.TargetSetpoints.VoltageV);
    }

    [Fact]
    public async Task RestorePreviousState_RerunsStartupRecipeWhenTripHappensDuringReadyLead()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 5, 26, 12, 0, 0, TimeSpan.Zero));
        var bus = new FakePcanBus();
        using var controller = CreateController(bus, clock, hvReadyLeadTimeMs: 5000);
        controller.RecoveryPolicy = TripRecoveryPolicy.RestorePreviousState;
        var recipe = new G2000StartupRecipe
        {
            Stage1VoltageV = 62.0,
            Stage1DurationMs = 6000,
            Stage2VoltageV = 42.0,
            Stage2HoldEnabled = true,
            EnterHvReadyBeforeRun = true,
            EnterHvOnAtStart = true
        };

        await controller.StartAutomaticSequenceAsync(recipe, CancellationToken.None);
        Assert.Equal("ReadyLead", controller.Snapshot.AutomaticStage);

        await controller.StopAsync(CancellationToken.None);
        await controller.ResetAsync(CancellationToken.None);

        Assert.Equal("ReadyLead", controller.Snapshot.AutomaticStage);
        Assert.Equal(62.0, controller.TargetSetpoints.VoltageV);

        clock.Advance(TimeSpan.FromMilliseconds(5000));
        await controller.RunSenderTickForTestAsync(CancellationToken.None);
        Assert.Equal("Stage1", controller.Snapshot.AutomaticStage);

        clock.Advance(TimeSpan.FromMilliseconds(6000));
        await controller.RunSenderTickForTestAsync(CancellationToken.None);
        Assert.Equal("Stage2", controller.Snapshot.AutomaticStage);
        Assert.Equal(42.0, controller.TargetSetpoints.VoltageV);
    }

    [Fact]
    public async Task PrepareRecoveryWhileInterlockOpen_ReplacesStage2HvOnBeforeInterlockCloses()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 5, 26, 12, 0, 0, TimeSpan.Zero));
        var bus = new FakePcanBus();
        using var controller = CreateController(bus, clock, hvReadyLeadTimeMs: 5000);
        controller.RecoveryPolicy = TripRecoveryPolicy.RestorePreviousState;
        var recipe = new G2000StartupRecipe
        {
            Stage1VoltageV = 62.0,
            Stage1DurationMs = 6000,
            Stage2VoltageV = 42.0,
            Stage2HoldEnabled = true,
            EnterHvReadyBeforeRun = true,
            EnterHvOnAtStart = true
        };

        await controller.StartAutomaticSequenceAsync(recipe, CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(5000));
        await controller.RunSenderTickForTestAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(6000));
        await controller.RunSenderTickForTestAsync(CancellationToken.None);
        Assert.Equal("Stage2", controller.Snapshot.AutomaticStage);
        Assert.Equal(G2000CanProtocol.CreateCanBusHvOnData(), bus.LastCommandData());

        var writtenFrameCountBeforeRecovery = bus.WrittenFrameCount;
        ((IG2000TripLatch)controller).LatchSoftwareTrip("Temperature limit trip");
        await ((IG2000RecoveryPreparation)controller).PrepareRecoveryWhileInterlockOpenAsync(CancellationToken.None);

        Assert.Equal("ReadyLead", controller.Snapshot.AutomaticStage);
        Assert.Equal(62.0, controller.TargetSetpoints.VoltageV);
        var commandFrames = bus.CommandFramesAfter(writtenFrameCountBeforeRecovery).ToArray();
        Assert.Single(commandFrames);
        Assert.Equal(G2000CanProtocol.CreateCanBusHvReadyData(), commandFrames[0]);
    }

    [Fact]
    public async Task PrepareRecoveryWhileInterlockOpen_ForcesHvReadyWhenRecipeStartsHvOnDirectly()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 5, 26, 12, 0, 0, TimeSpan.Zero));
        var bus = new FakePcanBus();
        using var controller = CreateController(bus, clock, hvReadyLeadTimeMs: 5000);
        controller.RecoveryPolicy = TripRecoveryPolicy.RestorePreviousState;
        var recipe = new G2000StartupRecipe
        {
            Stage1VoltageV = 62.0,
            Stage1DurationMs = 6000,
            Stage2VoltageV = 42.0,
            Stage2HoldEnabled = true,
            EnterHvReadyBeforeRun = false,
            EnterHvOnAtStart = true
        };

        await controller.StartAutomaticSequenceAsync(recipe, CancellationToken.None);
        Assert.Equal("Stage1", controller.Snapshot.AutomaticStage);
        Assert.Equal(G2000CanProtocol.CreateCanBusHvOnData(), bus.LastCommandData());
        clock.Advance(TimeSpan.FromMilliseconds(6000));
        await controller.RunSenderTickForTestAsync(CancellationToken.None);
        Assert.Equal("Stage2", controller.Snapshot.AutomaticStage);

        var writtenFrameCountBeforeRecovery = bus.WrittenFrameCount;
        ((IG2000TripLatch)controller).LatchSoftwareTrip("Temperature limit trip");
        await ((IG2000RecoveryPreparation)controller).PrepareRecoveryWhileInterlockOpenAsync(CancellationToken.None);

        Assert.Equal("ReadyLead", controller.Snapshot.AutomaticStage);
        Assert.False(controller.StartupRecipe.EnterHvReadyBeforeRun);
        var commandFrames = bus.CommandFramesAfter(writtenFrameCountBeforeRecovery).ToArray();
        Assert.Single(commandFrames);
        Assert.Equal(G2000CanProtocol.CreateCanBusHvReadyData(), commandFrames[0]);
    }

    [Fact]
    public async Task PrepareRecoveryWhileInterlockOpen_DoesNotArmHvOnBeforeInterlockClosesWhenLeadIsZero()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 5, 26, 12, 0, 0, TimeSpan.Zero));
        var bus = new FakePcanBus();
        using var controller = CreateController(bus, clock, hvReadyLeadTimeMs: 0);
        controller.RecoveryPolicy = TripRecoveryPolicy.RestorePreviousState;
        var recipe = new G2000StartupRecipe
        {
            Stage1VoltageV = 62.0,
            Stage1DurationMs = 6000,
            Stage2VoltageV = 42.0,
            Stage2HoldEnabled = true,
            EnterHvReadyBeforeRun = true,
            EnterHvOnAtStart = true
        };

        await controller.StartAutomaticSequenceAsync(recipe, CancellationToken.None);
        await controller.RunSenderTickForTestAsync(CancellationToken.None);
        Assert.Equal("Stage1", controller.Snapshot.AutomaticStage);
        Assert.Equal(G2000CanProtocol.CreateCanBusHvOnData(), bus.LastCommandData());
        clock.Advance(TimeSpan.FromMilliseconds(6000));
        await controller.RunSenderTickForTestAsync(CancellationToken.None);
        Assert.Equal("Stage2", controller.Snapshot.AutomaticStage);

        ((IG2000TripLatch)controller).LatchSoftwareTrip("Temperature limit trip");
        await ((IG2000RecoveryPreparation)controller).PrepareRecoveryWhileInterlockOpenAsync(CancellationToken.None);

        Assert.True(controller.IsTripLatched);
        await controller.RunSenderTickForTestAsync(CancellationToken.None);
        Assert.Equal("ReadyLead", controller.Snapshot.AutomaticStage);
        Assert.Equal(G2000CanProtocol.CreateCanBusHvReadyData(), bus.LastCommandData());

        await ((IG2000RecoveryPreparation)controller).CompletePreparedRecoveryAfterInterlockClosedAsync(CancellationToken.None);
        Assert.False(controller.IsTripLatched);
        await controller.RunSenderTickForTestAsync(CancellationToken.None);
        Assert.Equal("Stage1", controller.Snapshot.AutomaticStage);
        Assert.Equal(G2000CanProtocol.CreateCanBusHvOnData(), bus.LastCommandData());
    }

    [Fact]
    public async Task IncomingReservedActualFrame_UpdatesReservedOutputTelemetry()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 5, 26, 12, 0, 0, TimeSpan.Zero));
        var bus = new FakePcanBus();
        using var controller = CreateController(bus, clock, hvReadyLeadTimeMs: 0);
        var frame = CreateTwoFloatFrameForTest(123.5, 4.25);

        await controller.HandleIncomingMessageForTestAsync(G2000CanProtocol.GetReservedActualId(0), frame, CancellationToken.None);

        Assert.Equal(123.5, controller.Snapshot.ReservedOutputVoltageV!.Value, 2);
        Assert.Equal(4.25, controller.Snapshot.ReservedOutputCurrentA!.Value, 2);
        Assert.Equal(G2000CanProtocol.FormatFrame(frame), controller.Snapshot.ReservedActualFrameHex);

        var clone = controller.Snapshot.Clone();
        Assert.Equal(controller.Snapshot.ReservedOutputVoltageV, clone.ReservedOutputVoltageV);
        Assert.Equal(controller.Snapshot.ReservedOutputCurrentA, clone.ReservedOutputCurrentA);
    }

    [Fact]
    public async Task IncomingDcLinkActualFrame_UpdatesVoltageAndReservedCurrentTelemetry()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 5, 26, 12, 0, 0, TimeSpan.Zero));
        var bus = new FakePcanBus();
        using var controller = CreateController(bus, clock, hvReadyLeadTimeMs: 0);
        byte[] frame = [0x93, 0x3D, 0x8A, 0x3F, 0x00, 0x00, 0x00, 0x00];

        await controller.HandleIncomingMessageForTestAsync(G2000CanProtocol.GetDcLinkActualId(0), frame, CancellationToken.None);

        Assert.Equal(1.08, controller.Snapshot.DcLinkVoltageV!.Value, 2);
        Assert.Equal(0.0, controller.Snapshot.ReservedDcLinkCurrentA!.Value, 2);
        Assert.Equal(G2000CanProtocol.FormatFrame(frame), controller.Snapshot.DcLinkActualFrameHex);

        var clone = controller.Snapshot.Clone();
        Assert.Equal(controller.Snapshot.DcLinkVoltageV, clone.DcLinkVoltageV);
        Assert.Equal(controller.Snapshot.ReservedDcLinkCurrentA, clone.ReservedDcLinkCurrentA);
    }

    private static byte[] CreateTwoFloatFrameForTest(double first, double second)
    {
        var data = new byte[8];
        BitConverter.GetBytes((float)first).CopyTo(data, 0);
        BitConverter.GetBytes((float)second).CopyTo(data, 4);
        return data;
    }

    private static G2000CanController CreateController(FakePcanBus bus, MutableClock clock, int hvReadyLeadTimeMs)
    {
        var settings = new G2000CanSettings
        {
            HvReadyLeadTimeMs = hvReadyLeadTimeMs,
            CommandPeriodMs = 100,
            ReadPollIntervalMs = 16
        };

        return new G2000CanController(settings, bus, () => clock.Now, startBackgroundLoops: false);
    }

    private sealed class MutableClock(DateTimeOffset now)
    {
        public DateTimeOffset Now { get; private set; } = now;

        public void Advance(TimeSpan value)
        {
            Now = Now.Add(value);
        }
    }

    private sealed class FakePcanBus : IPcanBus
    {
        private readonly List<WrittenFrame> _writes = [];

        public int WrittenFrameCount => _writes.Count;

        public uint Initialize(ushort channel, ushort btr0Btr1, uint hwType, uint ioPort, ushort interrupt)
        {
            return PcanBasicNative.PcanErrorOk;
        }

        public uint Uninitialize(ushort channel)
        {
            return PcanBasicNative.PcanErrorOk;
        }

        public uint Write(ushort channel, ref PcanBasicNative.TPCANMsg messageBuffer)
        {
            _writes.Add(new WrittenFrame(messageBuffer.ID, messageBuffer.DATA.ToArray()));
            return PcanBasicNative.PcanErrorOk;
        }

        public uint Read(ushort channel, ref PcanBasicNative.TPCANMsg messageBuffer, out PcanBasicNative.TPCANTimestamp timestampBuffer)
        {
            timestampBuffer = default;
            return PcanBasicNative.PcanErrorReceiveQueueEmpty;
        }

        public byte[] LastCommandData()
        {
            return _writes.Last(frame => frame.Id == G2000CanProtocol.GetCommandId(0)).Data;
        }

        public IEnumerable<byte[]> CommandFramesAfter(int index)
        {
            return _writes
                .Skip(index)
                .Where(frame => frame.Id == G2000CanProtocol.GetCommandId(0))
                .Select(frame => frame.Data);
        }
    }

    private sealed record WrittenFrame(uint Id, byte[] Data);
}
