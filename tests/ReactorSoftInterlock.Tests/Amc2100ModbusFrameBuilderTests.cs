using ReactorSoftInterlock.Infrastructure.Gas;

namespace ReactorSoftInterlock.Tests;

public sealed class Amc2100ModbusFrameBuilderTests
{
    [Fact]
    public void BuildReadHoldingRegistersMatchesManualExample()
    {
        var frame = Amc2100ModbusFrameBuilder.BuildReadHoldingRegisters(0x01, 0x0000, 0x0002);

        Assert.Equal("01-03-00-00-00-02-C4-0B", BitConverter.ToString(frame));
    }

    [Fact]
    public void BuildWriteMultipleRegistersMatchesManualSetpointExample()
    {
        var frame = Amc2100ModbusFrameBuilder.BuildWriteMultipleRegisters(
            0x01,
            0x0002,
            Amc2100ModbusFrameBuilder.FloatToRegisters(10000.0f));

        Assert.Equal("01-10-00-02-00-02-04-46-1C-40-00-97-38", BitConverter.ToString(frame));
    }

    [Fact]
    public void RegistersToFloatMatchesManualExample()
    {
        var value = Amc2100ModbusFrameBuilder.RegistersToFloat(0x461C, 0x40CD);

        Assert.Equal(10000.2f, value, 1);
    }
}
