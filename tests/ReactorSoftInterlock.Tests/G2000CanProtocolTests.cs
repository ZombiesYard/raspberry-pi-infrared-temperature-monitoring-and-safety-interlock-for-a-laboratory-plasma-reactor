using ReactorSoftInterlock.Application.G2000;
using ReactorSoftInterlock.Infrastructure.Relay;
using ReactorSoftInterlock.Infrastructure.Settings;

namespace ReactorSoftInterlock.Tests;

public sealed class G2000CanProtocolTests
{
    [Fact]
    public void CreateCanBusStopData_MatchesVerifiedFrame()
    {
        Assert.Equal(new byte[] { 0x80, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00 }, G2000CanProtocol.CreateCanBusStopData());
    }

    [Fact]
    public void CreateCanBusHvReadyData_MatchesVerifiedFrame()
    {
        Assert.Equal(new byte[] { 0x82, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00 }, G2000CanProtocol.CreateCanBusHvReadyData());
    }

    [Fact]
    public void CreateCanBusHvOnData_MatchesVerifiedFrame()
    {
        Assert.Equal(new byte[] { 0x83, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00 }, G2000CanProtocol.CreateCanBusHvOnData());
    }

    [Fact]
    public void ParseStatusData_RecognizesVerifiedCanBusStates()
    {
        var stop = G2000CanProtocol.ParseStatusData([0x01, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00]);
        var ready = G2000CanProtocol.ParseStatusData([0x09, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00]);
        var on = G2000CanProtocol.ParseStatusData([0x0D, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00]);

        Assert.True(stop.Ready);
        Assert.False(stop.HvEnable);
        Assert.False(stop.HvOn);
        Assert.Equal(G2000ControlSource.CanBus, stop.Source);

        Assert.True(ready.HvEnable);
        Assert.False(ready.HvOn);

        Assert.True(on.HvEnable);
        Assert.True(on.HvOn);
    }

    [Fact]
    public void ResolveMode_PreservesLegacyDryRunFallback()
    {
        var dryRun = new RelaySettings { DryRun = true };
        var serial = new RelaySettings { DryRun = false };
        var can = new RelaySettings { DryRun = true, Mode = "G2000Can" };

        Assert.Equal(RelayControllerMode.DryRun, dryRun.ResolveMode());
        Assert.Equal(RelayControllerMode.Serial, serial.ResolveMode());
        Assert.Equal(RelayControllerMode.G2000Can, can.ResolveMode());
    }

    [Theory]
    [InlineData("UsbBus1", (ushort)0x51)]
    [InlineData("PCAN_USBBUS1", (ushort)0x51)]
    [InlineData("UsbBus4", (ushort)0x54)]
    public void TryParseChannel_AcceptsExpectedFormats(string channel, ushort expectedHandle)
    {
        Assert.True(PcanChannelParser.TryParse(channel, out var handle));
        Assert.Equal(expectedHandle, handle);
    }

    [Fact]
    public void CreateSetpointFrames_EncodeLittleEndianFloatPairs()
    {
        Assert.Equal(
            new byte[] { 0x00, 0x00, 0x2C, 0x42, 0x00, 0x00, 0x00, 0x00 },
            G2000CanProtocol.CreateDcLinkSetpointData(43.0));
        Assert.Equal(
            new byte[] { 0x00, 0x00, 0xDC, 0x42, 0x00, 0x00, 0x34, 0x42 },
            G2000CanProtocol.CreateInverterSetpointData(110.0, 45.0));
        Assert.Equal(
            new byte[] { 0x00, 0x00, 0x80, 0x3F, 0x00, 0x00, 0x00, 0x00 },
            G2000CanProtocol.CreatePulseSetpointData(1.0, 0.0));
    }

    [Fact]
    public void ParseTwoFloatFrame_DecodesVerifiedActuals()
    {
        var dcLink = G2000CanProtocol.ParseTwoFloatFrame([0x93, 0x3D, 0x8A, 0x3F, 0x00, 0x00, 0x00, 0x00]);
        var inverter = G2000CanProtocol.ParseTwoFloatFrame([0x51, 0x0B, 0xDC, 0x42, 0x00, 0x00, 0x34, 0x42]);
        var pulse = G2000CanProtocol.ParseTwoFloatFrame([0x05, 0x00, 0x80, 0x3F, 0x00, 0x00, 0x00, 0x00]);

        Assert.Equal(1.08, dcLink.First, 2);
        Assert.Equal(0.0, dcLink.Second, 2);
        Assert.Equal(110.02, inverter.First, 2);
        Assert.Equal(45.0, inverter.Second, 2);
        Assert.Equal(1.0, pulse.First, 2);
        Assert.Equal(0.0, pulse.Second, 2);
    }
}
