using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace StarResonanceACTPlugin
{
    internal class TcpTable
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct MIB_TCPROW_OWNER_PID
        {
            public uint state;
            public uint localAddr;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public byte[] localPort;
            public uint remoteAddr;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public byte[] remotePort;
            public int owningPid;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MIB_TCPTABLE_OWNER_PID
        {
            public uint dwNumEntries;
            public MIB_TCPROW_OWNER_PID table;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern uint GetExtendedTcpTable(
            IntPtr pTcpTable,
            ref int dwOutBufLen,
            bool sort,
            int ipVersion,
            TCP_TABLE_CLASS tblClass,
            uint reserved);

        public enum TCP_TABLE_CLASS
        {
            TCP_TABLE_BASIC_LISTENER,
            TCP_TABLE_BASIC_CONNECTIONS,
            TCP_TABLE_BASIC_ALL,
            TCP_TABLE_OWNER_PID_LISTENER,
            TCP_TABLE_OWNER_PID_CONNECTIONS,
            TCP_TABLE_OWNER_PID_ALL
        }

        public static Dictionary<int, int> GetTcpPortPidMap()
        {
            int bufferSize = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, true, 2, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0);

            IntPtr tcpTablePtr = Marshal.AllocHGlobal(bufferSize);
            try
            {
                if (GetExtendedTcpTable(tcpTablePtr, ref bufferSize, true, 2, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0) == 0)
                {
                    int rowSize = Marshal.SizeOf(typeof(MIB_TCPROW_OWNER_PID));
                    int numEntries = Marshal.ReadInt32(tcpTablePtr);
                    IntPtr rowPtr = IntPtr.Add(tcpTablePtr, 4);

                    var result = new Dictionary<int, int>();
                    for (int i = 0; i < numEntries; i++)
                    {
                        var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                        int port = (row.localPort[0] << 8) + row.localPort[1];
                        result[port] = row.owningPid;
                        rowPtr = IntPtr.Add(rowPtr, rowSize);
                    }
                    return result;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(tcpTablePtr);
            }
            return new Dictionary<int, int>();
        }
    }

}
