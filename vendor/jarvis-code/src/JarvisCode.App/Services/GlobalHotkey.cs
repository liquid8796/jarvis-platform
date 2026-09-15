using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace JarvisCode.App.Services;

/// <summary>What registering an accelerator came to — the reference's three answers plus "nothing to register".</summary>
public enum HotkeyOutcome
{
    /// <summary>The accelerator was empty: no shortcut.</summary>
    None,
    Registered,
    /// <summary>The OS refused the chord — another application owns it.</summary>
    RegistrationFailed,
    /// <summary>The accelerator names a key this platform cannot bind.</summary>
    InvalidAccelerator,
}

/// <summary>RegisterHotKey wrapper for the global Quick Entry chord.</summary>
public sealed class GlobalHotkey : IDisposable
{
    public const int WmHotkey = 0x0312;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkSpace = 0x20;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;
    private readonly int _id;
    private bool _registered;

    public GlobalHotkey(HwndSource source, int id)
    {
        _source = source;
        _id = id;
    }

    public bool IsRegistered => _registered;

    /// <summary>Registers Ctrl+Alt+Space. False when another app owns the chord.</summary>
    public bool RegisterCtrlAltSpace()
    {
        if (_registered)
        {
            return true;
        }

        _registered = RegisterHotKey(_source.Handle, _id, ModControl | ModAlt | ModNoRepeat, VkSpace);
        return _registered;
    }

    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    /// <summary>
    /// Registers an arbitrary accelerator ("Ctrl+Shift+Space"), replacing whatever
    /// was registered before. The reference's registration answers
    /// "registration-failed" when the OS refuses the chord (another app owns it)
    /// and "invalid-accelerator" when it names a key it cannot bind; both are
    /// reported here as the outcome so the recorder can show the right sentence.
    /// </summary>
    public HotkeyOutcome Register(string accelerator)
    {
        Unregister();
        if (QuickEntryShortcuts.Parse(accelerator) is not { } chord)
        {
            return HotkeyOutcome.None;
        }

        if (QuickEntryShortcuts.VirtualKey(chord.Key) is not { } vk)
        {
            return HotkeyOutcome.InvalidAccelerator;
        }

        uint modifiers = ModNoRepeat;
        if (chord.Control) modifiers |= ModControl;
        if (chord.Alt) modifiers |= ModAlt;
        if (chord.Shift) modifiers |= ModShift;
        if (chord.Win) modifiers |= ModWin;
        _registered = RegisterHotKey(_source.Handle, _id, modifiers, (uint)vk);
        return _registered ? HotkeyOutcome.Registered : HotkeyOutcome.RegistrationFailed;
    }

    public void Unregister()
    {
        if (_registered)
        {
            UnregisterHotKey(_source.Handle, _id);
            _registered = false;
        }
    }

    public bool Matches(IntPtr wParam) => wParam.ToInt32() == _id;

    public void Dispose() => Unregister();
}
