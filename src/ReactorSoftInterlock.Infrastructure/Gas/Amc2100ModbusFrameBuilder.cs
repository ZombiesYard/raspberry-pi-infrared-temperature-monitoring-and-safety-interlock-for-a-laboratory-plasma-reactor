using System.Buffers.Binary;

namespace ReactorSoftInterlock.Infrastructure.Gas;

public static class Amc2100ModbusFrameBuilder
{
    public static byte[] BuildReadHoldingRegisters(byte slaveAddress, ushort startAddress, ushort registerCount)
    {
        Span<byte> frame = stackalloc byte[8];
        frame[0] = slaveAddress;
        frame[1] = 0x03;
        BinaryPrimitives.WriteUInt16BigEndian(frame[2..4], startAddress);
        BinaryPrimitives.WriteUInt16BigEndian(frame[4..6], registerCount);
        WriteCrc(frame);
        return frame.ToArray();
    }

    public static byte[] BuildWriteSingleRegister(byte slaveAddress, ushort registerAddress, ushort value)
    {
        Span<byte> frame = stackalloc byte[8];
        frame[0] = slaveAddress;
        frame[1] = 0x06;
        BinaryPrimitives.WriteUInt16BigEndian(frame[2..4], registerAddress);
        BinaryPrimitives.WriteUInt16BigEndian(frame[4..6], value);
        WriteCrc(frame);
        return frame.ToArray();
    }

    public static byte[] BuildWriteMultipleRegisters(byte slaveAddress, ushort startAddress, ReadOnlySpan<ushort> values)
    {
        var frame = new byte[9 + (values.Length * 2)];
        frame[0] = slaveAddress;
        frame[1] = 0x10;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), startAddress);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4, 2), (ushort)values.Length);
        frame[6] = (byte)(values.Length * 2);

        for (var index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(7 + (index * 2), 2), values[index]);
        }

        WriteCrc(frame);
        return frame;
    }

    public static ushort[] FloatToRegisters(float value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return
        [
            BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(0, 2)),
            BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(2, 2))
        ];
    }

    public static float RegistersToFloat(ushort highRegister, ushort lowRegister)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(bytes[..2], highRegister);
        BinaryPrimitives.WriteUInt16BigEndian(bytes[2..], lowRegister);
        var buffer = bytes.ToArray();
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(buffer);
        }

        return BitConverter.ToSingle(buffer, 0);
    }

    public static ushort ComputeCrc(ReadOnlySpan<byte> dataWithoutCrc)
    {
        ushort crc = 0xFFFF;
        foreach (var current in dataWithoutCrc)
        {
            crc ^= current;
            for (var bit = 0; bit < 8; bit++)
            {
                var lsb = (crc & 0x0001) != 0;
                crc >>= 1;
                if (lsb)
                {
                    crc ^= 0xA001;
                }
            }
        }

        return crc;
    }

    public static bool ValidateCrc(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 3)
        {
            return false;
        }

        var expected = ComputeCrc(frame[..^2]);
        var actual = BinaryPrimitives.ReadUInt16LittleEndian(frame[^2..]);
        return expected == actual;
    }

    public static bool ValidateWriteSingleRegisterResponse(
        ReadOnlySpan<byte> request,
        ReadOnlySpan<byte> response)
    {
        return request.Length == 8 &&
               response.Length == 8 &&
               ValidateCrc(response) &&
               request.SequenceEqual(response);
    }

    public static bool ValidateWriteMultipleRegistersResponse(
        ReadOnlySpan<byte> request,
        ReadOnlySpan<byte> response)
    {
        return request.Length >= 9 &&
               response.Length == 8 &&
               ValidateCrc(response) &&
               response[..6].SequenceEqual(request[..6]);
    }

    private static void WriteCrc(Span<byte> frame)
    {
        var crc = ComputeCrc(frame[..^2]);
        BinaryPrimitives.WriteUInt16LittleEndian(frame[^2..], crc);
    }
}
