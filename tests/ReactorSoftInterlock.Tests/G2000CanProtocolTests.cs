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
        Assert.Equal(0x03, stop.Source);

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
}
