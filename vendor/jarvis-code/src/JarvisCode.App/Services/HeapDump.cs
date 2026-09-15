using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace JarvisCode.App.Services;

/// <summary>
/// /heapdump — writes a minidump of this process (private read/write memory,
/// data segments and handles: the managed heap without a full-memory monster)
/// for offline analysis in Visual Studio or WinDbg.
/// </summary>
public static class HeapDump
{
    private const uint MiniDumpWithDataSegs = 0x00000001;
    private const uint MiniDumpWithHandleData = 0x00000004;
    private const uint MiniDumpWithPrivateReadWriteMemory = 0x00000200;

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess, uint processId, SafeHandle hFile, uint dumpType,
        IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);

    /// <summary>Writes the dump; returns the error message, or null on success.</summary>
    public static string? Write(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var process = Process.GetCurrentProcess();
            using var file = File.Create(path);
            bool ok = MiniDumpWriteDump(
                process.Handle, (uint)process.Id, file.SafeFileHandle,
                MiniDumpWithPrivateReadWriteMemory | MiniDumpWithDataSegs | MiniDumpWithHandleData,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            return ok ? null : $"MiniDumpWriteDump failed (0x{Marshal.GetLastWin32Error():X})";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        $"jarvis-heapdump-{DateTime.Now:yyyyMMdd-HHmmss}.dmp");
}
