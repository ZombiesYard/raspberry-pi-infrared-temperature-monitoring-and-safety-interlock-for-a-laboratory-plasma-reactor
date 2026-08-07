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

    [Fact]
    public void WriteMultipleResponseMustConfirmRequestedAddressAndCount()
    {
        var request = Amc2100ModbusFrameBuilder.BuildWriteMultipleRegisters(
            0x01,
            0x0002,
            Amc2100ModbusFrameBuilder.FloatToRegisters(600f));
        var validResponse = BuildResponseWithCrc(request.AsSpan(0, 6));
        var wrongAddressData = request.AsSpan(0, 6).ToArray();
        wrongAddressData[3] = 0x03;
        var wrongAddressResponse = BuildResponseWithCrc(wrongAddressData);

        Assert.True(Amc2100ModbusFrameBuilder.ValidateWriteMultipleRegistersResponse(request, validResponse));
        Assert.False(Amc2100ModbusFrameBuilder.ValidateWriteMultipleRegistersResponse(request, wrongAddressResponse));
    }

    [Fact]
    public void WriteSingleResponseMustEchoEntireRequest()
    {
        var request = Amc2100ModbusFrameBuilder.BuildWriteSingleRegister(0x01, 0x000B, 0x0001);
        var wrongValue = Amc2100ModbusFrameBuilder.BuildWriteSingleRegister(0x01, 0x000B, 0x0002);

        Assert.True(Amc2100ModbusFrameBuilder.ValidateWriteSingleRegisterResponse(request, request));
        Assert.False(Amc2100ModbusFrameBuilder.ValidateWriteSingleRegisterResponse(request, wrongValue));
    }

    private static byte[] BuildResponseWithCrc(ReadOnlySpan<byte> data)
    {
        var response = new byte[data.Length + 2];
        data.CopyTo(response);
        var crc = Amc2100ModbusFrameBuilder.ComputeCrc(data);
        response[^2] = (byte)(crc & 0xFF);
        response[^1] = (byte)(crc >> 8);
        return response;
    }
}
