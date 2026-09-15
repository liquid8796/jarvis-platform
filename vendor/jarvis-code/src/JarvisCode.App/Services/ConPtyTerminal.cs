using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace JarvisCode.App.Services;

/// <summary>
/// A real Windows pseudo-console (ConPTY) session hosting a shell process.
/// Output arrives as raw VT text via <see cref="OutputReceived"/>; input goes
/// straight to the shell's stdin.
/// </summary>
public sealed class ConPtyTerminal : IDisposable
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const int StdInput = -10;
    private const int StdOutput = -11;
    private const int StdError = -12;
    private static readonly IntPtr PseudoConsoleAttribute = (IntPtr)0x20016;

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, Coord size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref StartupInfoEx lpStartupInfo, out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetStdHandle(int nStdHandle, IntPtr hHandle);

    private IntPtr _console;
    private IntPtr _attributeList;
    private ProcessInformation _process;
    private FileStream? _writer;
    private FileStream? _reader;
    private bool _disposed;

    public event Action<string>? OutputReceived;

    public event Action? Exited;

    public bool IsRunning => !_disposed && _process.hProcess != IntPtr.Zero;

    /// <summary>Spawns the shell in the pseudo-console. Throws Win32Exception on failure.</summary>
    public void Start(string commandLine, string workingDirectory, short columns = 100, short rows = 32)
    {
        if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0) ||
            !CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed");
        }

        var hr = CreatePseudoConsole(new Coord { X = columns, Y = rows }, inputRead, outputWrite, 0, out _console);
        if (hr != 0)
        {
            throw new System.ComponentModel.Win32Exception(hr, "CreatePseudoConsole failed");
        }

        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        _attributeList = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(_attributeList, 1, 0, ref size) ||
            !UpdateProcThreadAttribute(_attributeList, 0, PseudoConsoleAttribute, _console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Pseudo-console attribute failed");
        }

        // Whatever launched this app may have handed it a console and standard
        // handles. A console child prefers those over the pseudo-console, so its
        // output would never reach our pipe. Detach the console and blank the
        // std handles across the spawn — a GUI host uses neither — then restore.
        if (GetConsoleWindow() != IntPtr.Zero)
        {
            FreeConsole();
        }

        var saved = new[] { GetStdHandle(StdInput), GetStdHandle(StdOutput), GetStdHandle(StdError) };
        var startup = new StartupInfoEx { lpAttributeList = _attributeList };
        startup.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
        bool created;
        try
        {
            SetStdHandle(StdInput, IntPtr.Zero);
            SetStdHandle(StdOutput, IntPtr.Zero);
            SetStdHandle(StdError, IntPtr.Zero);
            created = CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                ExtendedStartupInfoPresent, IntPtr.Zero, workingDirectory, ref startup, out _process);
        }
        finally
        {
            SetStdHandle(StdInput, saved[0]);
            SetStdHandle(StdOutput, saved[1]);
            SetStdHandle(StdError, saved[2]);
        }

        if (!created)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"Could not start '{commandLine}'");
        }

        // The child owns its ends now.
        inputRead.Dispose();
        outputWrite.Dispose();

        _writer = new FileStream(inputWrite, FileAccess.Write);
        _reader = new FileStream(outputRead, FileAccess.Read);
        _ = Task.Run(ReadLoop);
    }

    private void ReadLoop()
    {
        var buffer = new byte[4096];
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[8192];
        try
        {
            while (true)
            {
                var read = _reader!.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                var count = decoder.GetChars(buffer, 0, read, chars, 0);
                if (count > 0)
                {
                    OutputReceived?.Invoke(new string(chars, 0, count));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The console closed; fall through to Exited.
        }

        Exited?.Invoke();
    }

    public void Write(string text)
    {
        if (_writer is null)
        {
            return;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            _writer.Write(bytes, 0, bytes.Length);
            _writer.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Sends Ctrl+C to the foreground program.</summary>
    public void Interrupt() => Write("\x03");

    public void Resize(short columns, short rows)
    {
        if (_console != IntPtr.Zero)
        {
            ResizePseudoConsole(_console, new Coord { X = Math.Max((short)20, columns), Y = Math.Max((short)5, rows) });
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_console != IntPtr.Zero)
        {
            ClosePseudoConsole(_console);
            _console = IntPtr.Zero;
        }

        if (_process.hProcess != IntPtr.Zero)
        {
            TerminateProcess(_process.hProcess, 0);
            CloseHandle(_process.hProcess);
            CloseHandle(_process.hThread);
            _process = default;
        }

        if (_attributeList != IntPtr.Zero)
        {
            DeleteProcThreadAttributeList(_attributeList);
            Marshal.FreeHGlobal(_attributeList);
            _attributeList = IntPtr.Zero;
        }

        _writer?.Dispose();
        _reader?.Dispose();
    }
}
