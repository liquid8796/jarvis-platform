using System.Diagnostics;
using System.Runtime.InteropServices;

namespace JarvisCode.Cli;

/// <summary>Keep a detached host from retaining the launcher's redirected console pipes.</summary>
internal static class DetachedProcessStart
{
    private const uint Inherit = 1;
    private static readonly object Gate = new();

    public static Process? Start(ProcessStartInfo start)
    {
        if (!OperatingSystem.IsWindows()) return Process.Start(start);
        lock (Gate)
        {
            var handles = new List<(nint Handle, uint Flags)>();
            try
            {
                // Process.Start creates separate inheritable startup pipes for
                // all three redirected streams. Original standard handles must
                // not also be inherited: they can be a parent's capture pipe,
                // whose reader otherwise never sees EOF while this host lives.
                foreach (var kind in new[] { -10, -11, -12 })
                {
                    var handle = GetStdHandle(kind);
                    if (handle == 0 || handle == -1 || handles.Any(item => item.Handle == handle) ||
                        !GetHandleInformation(handle, out var flags) || (flags & Inherit) == 0) continue;
                    if (!SetHandleInformation(handle, Inherit, 0))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    handles.Add((handle, flags));
                }
                return Process.Start(start);
            }
            finally
            {
                foreach (var (handle, flags) in handles) SetHandleInformation(handle, Inherit, flags & Inherit);
            }
        }
    }

    [DllImport("kernel32.dll")] private static extern nint GetStdHandle(int kind);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(nint handle, out uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(nint handle, uint mask, uint flags);
}
