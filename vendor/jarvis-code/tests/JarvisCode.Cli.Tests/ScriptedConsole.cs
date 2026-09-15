using System.Text;
using System.Threading.Channels;
using JarvisCode.Cli.Repl.Terminal;

namespace JarvisCode.Cli.Tests;

/// <summary>
/// A terminal for tests: keys are queued up front, output is captured, and the
/// reader completes once the script runs out — which is what ends the REPL's
/// loop the way a closed stdin does.
/// </summary>
internal sealed class ScriptedConsole : IConsole
{
    private readonly Channel<KeyPress> _keys = Channel.CreateUnbounded<KeyPress>();
    private readonly StringBuilder _output = new();

    public ScriptedConsole(int width = 100, int height = 40, params KeyPress[] keys)
    {
        Width = width;
        Height = height;
        foreach (var key in keys)
        {
            _keys.Writer.TryWrite(key);
        }

        _keys.Writer.TryComplete();
    }

    public int Width { get; }
    public int Height { get; }
    public bool IsInteractive => true;
    public bool SupportsAnsi => false;

    public event Action? Resized;

    /// <summary>Everything written, with the escape sequences stripped.</summary>
    public string Output => JarvisCode.Cli.Repl.Render.Ansi.Strip(_output.ToString());

    public void Write(string text) => _output.Append(text);

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

    /// <summary>Types a line of text and presses enter.</summary>
    public static IEnumerable<KeyPress> Type(string text)
    {
        foreach (var c in text)
        {
            yield return KeyPress.Typed(c.ToString());
        }
    }

    public static KeyPress Enter => new("enter");

    public static KeyPress Escape => new("escape");

    public void Unused() => Resized?.Invoke();
}
