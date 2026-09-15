using System.Net;
using System.Runtime.InteropServices;

namespace JarvisCode.Core.Ide;

/// <summary>Checks an explicit local listener against the lock's process; never probes arbitrary ports.</summary>
internal static class IdePortOwner
{
    internal static bool Matches(int port, int processId)
        => FindListenerProcess(port) == processId;

    internal static int? FindListenerProcess(int port)
        => FindListenerProcess(port, 2) ?? FindListenerProcess(port, 23);

    private static int? FindListenerProcess(int port, uint family)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var size = 0;
        var status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
        if (status != 122 || size <= 0) return null;
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(memory, ref size, false, family, 3, 0) != 0) return null;
            var count = Marshal.ReadInt32(memory);
            int? found = null;
            for (var index = 0; index < count; index++)
            {
                // MIB_TCPROW_OWNER_PID is 24 bytes; MIB_TCP6ROW_OWNER_PID is 56 bytes (tcpmib.h).
                var row = IntPtr.Add(memory, 4 + index * (family == 2 ? 24 : 56));
                var localPort = (ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(row, family == 2 ? 8 : 20));
                var owner = Marshal.ReadInt32(row, family == 2 ? 20 : 52);
                bool local;
                if (family == 2)
                {
                    var address = (uint)Marshal.ReadInt32(row, 4);
                    local = address == 0 || IPAddress.IsLoopback(new IPAddress(address));
                }
                else
                {
                    var address = new byte[16]; Marshal.Copy(row, address, 0, address.Length);
                    // A dual-stack wildcard listener also owns connections to the IPv4 loopback address we dial.
                    local = address.All(value => value == 0);
                }
                if (localPort == port && local)
                {
                    if (found is { } previous && previous != owner) return null;
                    found = owner;
                }
            }
            return found;
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool ordered, uint addressFamily, int tableClass, uint reserved);
}
