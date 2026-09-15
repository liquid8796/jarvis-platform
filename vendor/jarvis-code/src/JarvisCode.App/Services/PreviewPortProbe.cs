using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace JarvisCode.App.Services;

/// <summary>
/// The two real-world questions <see cref="PreviewPorts"/> asks: can this port be
/// bound, and who is holding it. The reference answers both from its Electron main
/// process — a bind with retries (its <c>cor</c>) and a native
/// <c>listTcpListeners</c> binding behind <c>por</c>/<c>mor</c> — and takes the
/// unnamed branch whenever the native side cannot answer, which is what this does
/// when the table cannot be read.
/// </summary>
public static class PreviewPortProbe
{
    /// <summary>The reference's <c>uor</c>: how much of a process name it prints.</summary>
    private const int ProcessNameLimit = 32;

    /// <summary>
    /// Binds <paramref name="port"/> to find out whether it is free, then releases it.
    /// Passing 0 asks the OS for a fresh port and reports which one it gave.
    ///
    /// Both the wildcard and the loopback address are tried, because a dev server
    /// binds one or the other and a port that either of them refuses is not one this
    /// configuration can have. Releasing before the server starts leaves the same
    /// short race the reference leaves.
    /// </summary>
    public static PortBindResult Bind(int port)
    {
        var wildcard = TryBind(IPAddress.Any, port);
        if (wildcard.Outcome != PortBindOutcome.Bound)
            return wildcard;

        // With a fresh port the OS just told us it is free; asking twice would ask
        // about a different port the second time.
        if (port == 0)
            return wildcard;

        var loopback = TryBind(IPAddress.Loopback, port);
        return loopback.Outcome == PortBindOutcome.Bound ? wildcard : loopback;
    }

    private static PortBindResult TryBind(IPAddress address, int port)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(address, port);
            listener.Start();
            var bound = ((IPEndPoint)listener.LocalEndpoint).Port;
            return PortBindResult.Bound(bound);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied)
        {
            return PortBindResult.Reserved;
        }
        catch (SocketException)
        {
            return PortBindResult.InUse;
        }
        finally
        {
            listener?.Stop();
        }
    }

    /// <summary>
    /// The reference's <c>mor</c>: <c>"name" (PID n)</c> for whoever is listening on
    /// the port, with <c> and n other process(es)</c> when several are, and nothing at
    /// all when the table cannot be read — which is the branch the reference itself
    /// takes when its native binding is unavailable.
    /// </summary>
    public static string? Occupant(int port)
    {
        var pids = ListeningPids(port);
        if (pids.Count == 0)
            return null;

        // The reference prefers a row that actually names a process.
        var pid = pids.FirstOrDefault(p => p != 0);
        var name = pid == 0 ? null : ProcessName(pid);
        var quoted = string.IsNullOrEmpty(name) ? "" : $"\"{name}\" ";
        var identity = pid == 0 ? "unknown PID" : $"PID {pid}";
        var others = pids.Distinct().Count() - 1;
        return $"{quoted}({identity}){(others > 0 ? $" and {others} other process(es)" : "")}";
    }

    /// <summary>The reference's <c>lor</c>/<c>uor</c>: a plain, short, quotable name.</summary>
    private static string? ProcessName(int pid)
    {
        string raw;
        try
        {
            using var process = Process.GetProcessById(pid);
            raw = process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }

        var builder = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '+' or '-' or ' ')
                builder.Append(ch);
        }

        var trimmed = builder.ToString();
        if (trimmed.Length > ProcessNameLimit)
            trimmed = trimmed[..ProcessNameLimit];
        trimmed = trimmed.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// The processes listening on <paramref name="port"/> at a local address, which is
    /// the reference's <c>por</c> filtered by its <c>dor</c> (the wildcard and loopback
    /// addresses in both families).
    /// </summary>
    private static IReadOnlyList<int> ListeningPids(int port)
    {
        if (!OperatingSystem.IsWindows())
            return [];

        var pids = new List<int>();
        try
        {
            pids.AddRange(Rows(AfInet, port));
            pids.AddRange(Rows(AfInet6, port));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }

        return pids;
    }

    private const int AfInet = 2;
    private const int AfInet6 = 23;

    /// <summary>TCP_TABLE_OWNER_PID_LISTENER.</summary>
    private const int ListenerTable = 3;

    private const int NoError = 0;
    private const int ErrorInsufficientBuffer = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(
        IntPtr table, ref int size, bool order, int family, int tableClass, int reserved);

    private static IEnumerable<int> Rows(int family, int port)
    {
        var size = 0;
        var status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, ListenerTable, 0);
        if (status != ErrorInsufficientBuffer && status != NoError)
            return [];
        if (size <= 0)
            return [];

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, family, ListenerTable, 0) != NoError)
                return [];

            var count = Marshal.ReadInt32(buffer);
            // MIB_TCPROW_OWNER_PID is six DWORDs; MIB_TCP6ROW_OWNER_PID is two
            // 16-byte addresses plus six DWORDs.
            var rowSize = family == AfInet ? 24 : 56;
            var portOffset = family == AfInet ? 8 : 20;
            var pidOffset = rowSize - 4;
            var addressOffset = family == AfInet ? 4 : 0;

            var found = new List<int>();
            for (var i = 0; i < count; i++)
            {
                var row = buffer + 4 + (i * rowSize);
                var rowPort = NetworkPort(Marshal.ReadInt32(row + portOffset));
                if (rowPort != port)
                    continue;
                if (!IsLocalAddress(row + addressOffset, family))
                    continue;
                found.Add(Marshal.ReadInt32(row + pidOffset));
            }

            return found;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The port sits in the low two bytes, in network order.</summary>
    private static int NetworkPort(int value)
    {
        var low = (value >> 8) & 0xFF;
        var high = value & 0xFF;
        return (high << 8) | low;
    }

    /// <summary>
    /// The reference's <c>dor</c>: the unspecified and loopback addresses of either
    /// family, including an IPv4-mapped loopback.
    /// </summary>
    private static bool IsLocalAddress(IntPtr address, int family)
    {
        if (family == AfInet)
        {
            var bytes = new byte[4];
            Marshal.Copy(address, bytes, 0, 4);
            // 0.0.0.0 and anything in 127.0.0.0/8.
            return (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0) || bytes[0] == 127;
        }

        var v6 = new byte[16];
        Marshal.Copy(address, v6, 0, 16);
        var allZero = v6.All(static b => b == 0);
        if (allZero)
            return true;

        // ::1
        if (v6.Take(15).All(static b => b == 0) && v6[15] == 1)
            return true;

        // ::ffff:127.x.x.x
        return v6.Take(10).All(static b => b == 0) && v6[10] == 0xFF && v6[11] == 0xFF && v6[12] == 127;
    }
}
