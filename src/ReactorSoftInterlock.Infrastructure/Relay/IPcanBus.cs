namespace ReactorSoftInterlock.Infrastructure.Relay;

internal interface IPcanBus
{
    uint Initialize(ushort channel, ushort btr0Btr1, uint hwType, uint ioPort, ushort interrupt);

    uint Uninitialize(ushort channel);

    uint Write(ushort channel, ref PcanBasicNative.TPCANMsg messageBuffer);

    uint Read(ushort channel, ref PcanBasicNative.TPCANMsg messageBuffer, out PcanBasicNative.TPCANTimestamp timestampBuffer);
}
