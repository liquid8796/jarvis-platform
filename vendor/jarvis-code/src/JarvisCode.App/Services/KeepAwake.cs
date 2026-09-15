using System.Runtime.InteropServices;

namespace JarvisCode.App.Services;

/// <summary>
/// "Keep computer awake": holds the system awake while a response or routine
/// runs, released the moment nothing is. Call from one thread (the UI's) —
/// SetThreadExecutionState is per-thread state.
/// </summary>
internal static class KeepAwake
{
    private const uint Continuous = 0x80000000;
    private const uint SystemRequired = 0x00000001;

    private static bool _held;

    public static void SetActive(bool active)
    {
        if (active == _held)
        {
            return;
        }

        _held = active;
        SetThreadExecutionState(active ? Continuous | SystemRequired : Continuous);
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);
}
