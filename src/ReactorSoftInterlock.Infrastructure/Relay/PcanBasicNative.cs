using System.Runtime.InteropServices;

namespace ReactorSoftInterlock.Infrastructure.Relay;

internal static class PcanBasicNative
{
    private const string LibraryName = "PCANBasic.dll";

    internal const uint PcanErrorOk = 0x00000;
    internal const ushort PcanBaud125K = 0x031C;
    internal const byte PcanMessageStandard = 0x00;

    [StructLayout(LayoutKind.Sequential)]
    internal struct TPCANMsg
    {
        internal uint ID;
        internal byte MSGTYPE;
        internal byte LEN;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        internal byte[] DATA;
    }

    [DllImport(LibraryName, EntryPoint = "CAN_Initialize")]
    internal static extern uint Initialize(ushort channel, ushort btr0Btr1, uint hwType, uint ioPort, ushort interrupt);

    [DllImport(LibraryName, EntryPoint = "CAN_Uninitialize")]
    internal static extern uint Uninitialize(ushort channel);

    [DllImport(LibraryName, EntryPoint = "CAN_Write")]
    internal static extern uint Write(ushort channel, ref TPCANMsg messageBuffer);
}
