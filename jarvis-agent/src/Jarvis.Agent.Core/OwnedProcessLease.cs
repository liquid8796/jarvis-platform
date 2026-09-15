using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace Jarvis.Agent.Core;
/// <summary>A Windows job object closes all contained descendants when a managed build/test job ends.</summary>
internal sealed class OwnedProcessLease : IDisposable
{
    private readonly object _sync = new();
    private SafeFileHandle? _job;
    public OwnedProcessLease()
    {
        if (!OperatingSystem.IsWindows()) return;
        _job = CreateJobObject(IntPtr.Zero, null);
        if (_job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new ExtendedLimitInformation { Basic = new BasicLimitInformation { Flags = 0x2000 /* KILL_ON_JOB_CLOSE */ } };
        if (!SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        { var error = Marshal.GetLastWin32Error(); _job.Dispose(); throw new Win32Exception(error); }
    }
    public void Attach(Process process)
    {
        if (_job is not null && !AssignProcessToJobObject(_job, process.Handle))
        { var error = Marshal.GetLastWin32Error(); try { process.Kill(true); } catch (InvalidOperationException) { } throw new Win32Exception(error); }
    }
    public void Stop()
    {
        lock (_sync) if (_job is { IsClosed: false, IsInvalid: false }) TerminateJobObject(_job, 1);
    }
    public void Dispose() { lock (_sync) { _job?.Dispose(); _job = null; } }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimitInformation
    { public long ProcessTime, JobTime; public uint Flags; public nuint MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcesses; public nuint Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimitInformation
    { public BasicLimitInformation Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimitInformation info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
}
