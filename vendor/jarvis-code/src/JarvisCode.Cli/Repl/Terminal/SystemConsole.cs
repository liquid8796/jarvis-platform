using System.IO;
using System.Text;
using System.Threading.Channels;

namespace JarvisCode.Cli.Repl.Terminal;

/// <summary>
/// The real terminal: raw key reads on a background thread, decoded into the
/// reference's key names, with a paste heuristic — a burst of key events that
/// arrive together and carry a newline or more than a handful of characters is
/// one <see cref="KeyPress.Paste"/> rather than typed keys.
/// </summary>
internal sealed class SystemConsole : IConsole, IDisposable
{
    private readonly Channel<KeyPress> _keys = Channel.CreateUnbounded<KeyPress>();
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _reader;
    private int _width;
    private int _height;

    public SystemConsole()
    {
        IsInteractive = !Console.IsOutputRedirected && !Console.IsInputRedirected;
        SupportsAnsi = IsInteractive && Environment.GetEnvironmentVariable("NO_COLOR") is null;
        if (IsInteractive)
        {
            try
            {
                Console.TreatControlCAsInput = true;
            }
            catch (IOException)
            {
                IsInteractive = false;
            }
        }

        ReadSize();
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "jarvis-keys" };
        if (IsInteractive)
        {
            _reader.Start();
        }
        else
        {
            _keys.Writer.TryComplete();
        }
    }

    public int Width => _width;
    public int Height => _height;
    public bool IsInteractive { get; private set; }
    public bool SupportsAnsi { get; }
    public event Action? Resized;

    public void Write(string text)
    {
        Console.Out.Write(text);
        Console.Out.Flush();
    }

    public async ValueTask<KeyPress?> ReadKeyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _keys.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    private void ReadSize()
    {
        try
        {
            _width = Math.Max(20, Console.WindowWidth);
            _height = Math.Max(5, Console.WindowHeight);
        }
        catch (IOException)
        {
            _width = 80;
            _height = 24;
        }
    }

    private void ReadLoop()
    {
        var writer = _keys.Writer;
        while (!_stop.IsCancellationRequested)
        {
            ConsoleKeyInfo first;
            try
            {
                if (!Console.KeyAvailable)
                {
                    Thread.Sleep(15);
                    CheckResize();
                    continue;
                }

                first = Console.ReadKey(intercept: true);
            }
            catch (InvalidOperationException)
            {
                break;
            }
            catch (IOException)
            {
                break;
            }

            // Drain whatever arrived with it: a paste lands as one burst.
            var burst = new List<ConsoleKeyInfo> { first };
            try
            {
                while (Console.KeyAvailable && burst.Count < 100_000)
                {
                    burst.Add(Console.ReadKey(intercept: true));
                }
            }
            catch (InvalidOperationException)
            {
            }

            foreach (var press in Decode(burst))
            {
                writer.TryWrite(press);
            }
        }

        writer.TryComplete();
    }

    private void CheckResize()
    {
        int w, h;
        try
        {
            w = Console.WindowWidth;
            h = Console.WindowHeight;
        }
        catch (IOException)
        {
            return;
        }

        if (w != _width || h != _height)
        {
            _width = Math.Max(20, w);
            _height = Math.Max(5, h);
            Resized?.Invoke();
        }
    }

    /// <summary>
    /// A burst that looks like a paste — more than one printable event and
    /// either a newline or a good run of characters — becomes one paste press;
    /// anything else decodes key by key.
    /// </summary>
    internal static IEnumerable<KeyPress> Decode(IReadOnlyList<ConsoleKeyInfo> burst)
    {
        if (burst.Count > 1 && LooksLikePaste(burst))
        {
            var text = new StringBuilder();
            foreach (var info in burst)
            {
                if (info.Key == ConsoleKey.Enter)
                {
                    text.Append('\n');
                }
                else if (info.Key == ConsoleKey.Tab)
                {
                    text.Append('\t');
                }
                else if (info.KeyChar != '\0' && !char.IsControl(info.KeyChar))
                {
                    text.Append(info.KeyChar);
                }
            }

            yield return KeyPress.Paste(text.ToString());
            yield break;
        }

        foreach (var info in burst)
        {
            yield return FromKeyInfo(info);
        }
    }

    private static bool LooksLikePaste(IReadOnlyList<ConsoleKeyInfo> burst)
    {
        int printable = 0;
        bool newline = false;
        foreach (var info in burst)
        {
            if ((info.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) != 0)
            {
                return false;
            }

            if (info.Key == ConsoleKey.Enter)
            {
                newline = true;
            }
            else if (info.KeyChar != '\0' && !char.IsControl(info.KeyChar))
            {
                printable++;
            }
            else if (info.Key != ConsoleKey.Tab)
            {
                return false;
            }
        }

        return newline || printable >= 8;
    }

    internal static KeyPress FromKeyInfo(ConsoleKeyInfo info)
    {
        bool ctrl = (info.Modifiers & ConsoleModifiers.Control) != 0;
        bool alt = (info.Modifiers & ConsoleModifiers.Alt) != 0;
        bool shift = (info.Modifiers & ConsoleModifiers.Shift) != 0;
        string? name = info.Key switch
        {
            ConsoleKey.Enter => "enter",
            ConsoleKey.Escape => "escape",
            ConsoleKey.Tab => "tab",
            ConsoleKey.Backspace => "backspace",
            ConsoleKey.Delete => "delete",
            ConsoleKey.UpArrow => "up",
            ConsoleKey.DownArrow => "down",
            ConsoleKey.LeftArrow => "left",
            ConsoleKey.RightArrow => "right",
            ConsoleKey.PageUp => "pageup",
            ConsoleKey.PageDown => "pagedown",
            ConsoleKey.Home => "home",
            ConsoleKey.End => "end",
            ConsoleKey.Insert => "insert",
            ConsoleKey.F1 => "f1",
            ConsoleKey.F2 => "f2",
            ConsoleKey.F3 => "f3",
            ConsoleKey.F4 => "f4",
            ConsoleKey.F5 => "f5",
            ConsoleKey.F6 => "f6",
            ConsoleKey.F7 => "f7",
            ConsoleKey.F8 => "f8",
            ConsoleKey.F9 => "f9",
            ConsoleKey.F10 => "f10",
            ConsoleKey.F11 => "f11",
            ConsoleKey.F12 => "f12",
            _ => null,
        };

        if (name is not null)
        {
            // Ctrl+J arrives as Enter with a '\n' char on Windows; the binding
            // table keys it as ctrl+j (chat:newline), so keep it distinct.
            if (info.Key == ConsoleKey.Enter && info.KeyChar == '\n' && !shift)
            {
                return new KeyPress("j", Ctrl: true);
            }

            return new KeyPress(name, ctrl, alt, shift);
        }

        char c = info.KeyChar;
        if (ctrl || alt)
        {
            // With a modifier the char is a control code (or nothing); the key
            // enum names the letter.
            string keyName = info.Key switch
            {
                >= ConsoleKey.A and <= ConsoleKey.Z => char.ToLowerInvariant((char)('A' + (info.Key - ConsoleKey.A))).ToString(),
                >= ConsoleKey.D0 and <= ConsoleKey.D9 => ((char)('0' + (info.Key - ConsoleKey.D0))).ToString(),
                ConsoleKey.OemMinus => shift ? "_" : "-",
                ConsoleKey.Oem4 => "[",
                ConsoleKey.Oem6 => "]",
                ConsoleKey.Oem5 => "\\",
                ConsoleKey.Oem2 => "/",
                ConsoleKey.OemPeriod => ".",
                ConsoleKey.OemComma => ",",
                ConsoleKey.Spacebar => " ",
                _ => c != '\0' && !char.IsControl(c) ? c.ToString() : info.Key.ToString().ToLowerInvariant(),
            };
            // "ctrl+shift+-" and "ctrl+_" are both bound to undo; a shifted
            // minus reads as "_" so either spelling reaches the table.
            if (keyName == "_")
            {
                shift = false;
            }

            return new KeyPress(keyName, ctrl, alt, shift);
        }

        if (c == '\0' || char.IsControl(c))
        {
            return new KeyPress(info.Key.ToString().ToLowerInvariant(), ctrl, alt, shift);
        }

        // A plain printable character: the key is the character itself, and
        // shift is already expressed by its case.
        return new KeyPress(c.ToString()) { Text = c.ToString() };
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (IsInteractive)
        {
            try
            {
                Console.TreatControlCAsInput = false;
            }
            catch (IOException)
            {
            }
        }
    }
}
