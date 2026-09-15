namespace JarvisCode.Cli.Repl.Terminal;

/// <summary>
/// The terminal the REPL draws on and reads from, behind an interface so the
/// whole REPL — key dispatch, the editor, every renderer and dialog — runs
/// against a scripted console in tests without a terminal attached.
/// </summary>
internal interface IConsole
{
    int Width { get; }
    int Height { get; }

    /// <summary>False when stdout is a pipe: no cursor moves, no colour, no live region.</summary>
    bool IsInteractive { get; }

    /// <summary>Whether the terminal takes ANSI colour and cursor sequences.</summary>
    bool SupportsAnsi { get; }

    void Write(string text);

    /// <summary>The next key press, or null when input has ended.</summary>
    ValueTask<KeyPress?> ReadKeyAsync(CancellationToken cancellationToken);

    /// <summary>Raised when the terminal is resized.</summary>
    event Action? Resized;
}
