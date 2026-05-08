using System.Globalization;
using ReactorSoftInterlock.Application.G2000;

namespace ReactorSoftInterlock.Infrastructure.Relay;

public static class G2000CanProtocol
{
    public const uint StatusBaseId = 0x180;
    public const uint CommandBaseId = 0x200;
    public const uint DcLinkActualBaseId = 0x280;
    public const uint InverterActualBaseId = 0x281;
    public const uint DcLinkSetpointBaseId = 0x300;
    public const uint InverterSetpointBaseId = 0x301;
    public const uint ReservedActualBaseId = 0x380;
    public const uint PulseActualBaseId = 0x381;
    public const uint PulseSetpointBaseId = 0x401;

    public const byte InternalSource = 0x01;
    public const byte CanBusSource = 0x03;
    public const byte CommandWriteEnable = 0x80;
    public const byte HvOnBit = 0x01;
    public const byte HvEnableBit = 0x02;
    public const byte ErrorCodeInterlock = 0x08;
    public const byte ErrorCodeExternalCanTimeout = 0x09;

    public static uint GetStatusId(byte nodeId) => StatusBaseId + ValidateNodeId(nodeId);

    public static uint GetCommandId(byte nodeId) => CommandBaseId + ValidateNodeId(nodeId);

    public static uint GetDcLinkActualId(byte nodeId) => DcLinkActualBaseId + ValidateNodeId(nodeId);

    public static uint GetInverterActualId(byte nodeId) => InverterActualBaseId + ValidateNodeId(nodeId);

    public static uint GetDcLinkSetpointId(byte nodeId) => DcLinkSetpointBaseId + ValidateNodeId(nodeId);

    public static uint GetInverterSetpointId(byte nodeId) => InverterSetpointBaseId + ValidateNodeId(nodeId);

    public static uint GetReservedActualId(byte nodeId) => ReservedActualBaseId + ValidateNodeId(nodeId);

    public static uint GetPulseActualId(byte nodeId) => PulseActualBaseId + ValidateNodeId(nodeId);

    public static uint GetPulseSetpointId(byte nodeId) => PulseSetpointBaseId + ValidateNodeId(nodeId);

    public static byte[] CreateCommandData(byte source, bool hvEnable, bool hvOn)
    {
        var command = CommandWriteEnable;
        if (hvOn)
        {
            command |= HvOnBit;
        }

        if (hvEnable)
        {
            command |= HvEnableBit;
        }

        return [command, 0x00, source, 0x00, 0x00, 0x00, 0x00, 0x00];
    }

    public static byte[] CreateCanBusStopData() => CreateCommandData(CanBusSource, hvEnable: false, hvOn: false);

    public static byte[] CreateCanBusHvReadyData() => CreateCommandData(CanBusSource, hvEnable: true, hvOn: false);

    public static byte[] CreateCanBusHvOnData() => CreateCommandData(CanBusSource, hvEnable: true, hvOn: true);

    public static byte[] CreateDcLinkSetpointData(double voltageV, double auxiliaryValue = 0.0) => CreateTwoFloatFrame(voltageV, auxiliaryValue);

    public static byte[] CreateInverterSetpointData(double frequencyKhz, double dutyPercent) => CreateTwoFloatFrame(frequencyKhz, dutyPercent);

    public static byte[] CreatePulseSetpointData(double tonMs, double toffMs) => CreateTwoFloatFrame(tonMs, toffMs);

    public static G2000Status ParseStatusData(ReadOnlySpan<byte> data)
    {
        if (data.Length != 8)
        {
            throw new ArgumentException("G2000 status frames must contain exactly 8 bytes.", nameof(data));
        }

        var flags = data[0];
        var source = data[2] switch
        {
            0x00 => G2000ControlSource.FrontPanel,
            0x01 => G2000ControlSource.Internal,
            0x02 => G2000ControlSource.External,
            0x03 => G2000ControlSource.CanBus,
            _ => G2000ControlSource.Unknown
        };

        return new G2000Status(
            Ready: (flags & 0x01) != 0,
            Fault: (flags & 0x02) != 0,
            HvOn: (flags & 0x04) != 0,
            HvEnable: (flags & 0x08) != 0,
            Source: source,
            ErrorCode: data[3],
            ErrorText: DescribeErrorCode(data[3]));
    }

    public static (double First, double Second) ParseTwoFloatFrame(ReadOnlySpan<byte> data)
    {
        if (data.Length != 8)
        {
            throw new ArgumentException("G2000 float frames must contain exactly 8 bytes.", nameof(data));
        }

        return (ReadFloat32(data[..4]), ReadFloat32(data[4..8]));
    }

    public static string DescribeErrorCode(byte errorCode)
    {
        return errorCode switch
        {
            0x00 => "No fault",
            0x07 => "Internal CAN timeout",
            0x08 => "Interlock fault",
            0x09 => "External CAN timeout",
            _ => $"Error 0x{errorCode:X2}"
        };
    }

    public static string FormatFrame(ReadOnlySpan<byte> data)
    {
        return string.Join(" ", data.ToArray().Select(static value => value.ToString("X2", CultureInfo.InvariantCulture)));
    }

    private static byte ValidateNodeId(byte nodeId)
    {
        if (nodeId > 0x7E)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeId), nodeId, "G2000 CAN node IDs must be in the range 0x00..0x7E.");
        }

        return nodeId;
    }

    private static byte[] CreateTwoFloatFrame(double firstValue, double secondValue)
    {
        var data = new byte[8];
        BitConverter.GetBytes((float)firstValue).CopyTo(data, 0);
        BitConverter.GetBytes((float)secondValue).CopyTo(data, 4);
        return data;
    }

    private static float ReadFloat32(ReadOnlySpan<byte> data)
    {
        return BitConverter.ToSingle(data);
    }
}

public readonly record struct G2000Status(
    bool Ready,
    bool Fault,
    bool HvOn,
    bool HvEnable,
    G2000ControlSource Source,
    byte ErrorCode,
    string ErrorText);
