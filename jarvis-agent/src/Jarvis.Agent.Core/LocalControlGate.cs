namespace Jarvis.Agent.Core;

/// <summary>
/// An explicit, process-local control grant. No wall-clock expiry or persisted auto-arm.
/// Transport reconnects do not change the choice; Pause/Disconnect/Exit revoke it.
/// Tool deadlines and per-action approvals are independent of this gate.
/// </summary>
public sealed class LocalControlGate
{
    private int _armed;
    public bool IsArmed => Volatile.Read(ref _armed) == 1;
    public event Action<bool>? Changed;

    public void Arm() => Set(true);
    public void Disarm() => Set(false);

    private void Set(bool armed)
    {
        if (Interlocked.Exchange(ref _armed, armed ? 1 : 0) != (armed ? 1 : 0))
            Changed?.Invoke(armed);
    }
}
