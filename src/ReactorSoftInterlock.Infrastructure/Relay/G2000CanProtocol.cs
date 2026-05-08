namespace ReactorSoftInterlock.Infrastructure.Relay;

public static class G2000CanProtocol
{
    public const uint CommandBaseId = 0x200;
    public const byte InternalSource = 0x01;
    public const byte CanBusSource = 0x03;
    public const byte CommandWriteEnable = 0x80;
    public const byte HvOnBit = 0x01;
    public const byte HvEnableBit = 0x02;

    public static uint GetCommandId(byte nodeId)
    {
        ValidateNodeId(nodeId);
        return CommandBaseId + nodeId;
    }

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

    public static G2000Status ParseStatusData(ReadOnlySpan<byte> data)
    {
        if (data.Length != 8)
        {
            throw new ArgumentException("G2000 status frames must contain exactly 8 bytes.", nameof(data));
        }

        var flags = data[0];
        return new G2000Status(
            Ready: (flags & 0x01) != 0,
            Fault: (flags & 0x02) != 0,
            HvOn: (flags & 0x04) != 0,
            HvEnable: (flags & 0x08) != 0,
            Source: data[2],
            ErrorCode: data[3]);
    }

    private static void ValidateNodeId(byte nodeId)
    {
        if (nodeId > 0x7E)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeId), nodeId, "G2000 CAN node IDs must be in the range 0x00..0x7E.");
        }
    }
}

public readonly record struct G2000Status(
    bool Ready,
    bool Fault,
    bool HvOn,
    bool HvEnable,
    byte Source,
    byte ErrorCode);
