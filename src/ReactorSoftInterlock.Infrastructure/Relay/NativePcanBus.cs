namespace ReactorSoftInterlock.Infrastructure.Relay;

internal sealed class NativePcanBus : IPcanBus
{
    public uint Initialize(ushort channel, ushort btr0Btr1, uint hwType, uint ioPort, ushort interrupt)
    {
        return PcanBasicNative.Initialize(channel, btr0Btr1, hwType, ioPort, interrupt);
    }

    public uint Uninitialize(ushort channel)
    {
        return PcanBasicNative.Uninitialize(channel);
    }

    public uint Write(ushort channel, ref PcanBasicNative.TPCANMsg messageBuffer)
    {
        return PcanBasicNative.Write(channel, ref messageBuffer);
    }

    public uint Read(ushort channel, ref PcanBasicNative.TPCANMsg messageBuffer, out PcanBasicNative.TPCANTimestamp timestampBuffer)
    {
        return PcanBasicNative.Read(channel, ref messageBuffer, out timestampBuffer);
    }
}
