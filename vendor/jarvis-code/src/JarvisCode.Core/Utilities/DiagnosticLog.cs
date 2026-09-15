namespace JarvisCode.Core.Utilities;

/// <summary>
/// Lifecycle trace for streaming turns, so a stream that dies mid-answer leaves
/// evidence behind. Silent until a host attaches a sink, which keeps Core and the
/// providers free of any file or logging dependency.
/// </summary>
/// <remarks>
/// Lines carry metadata only — never prompt text, tool arguments, tool results or
/// API keys — so the file stays safe to attach to a bug report.
/// </remarks>
public static class DiagnosticLog
{
    private static Action<string>? _sink;

    /// <summary>True once a host is listening; lets callers skip building a line.</summary>
    public static bool IsEnabled => Volatile.Read(ref _sink) is not null;

    /// <summary>Attaches the host's writer, or detaches with null.</summary>
    public static void Attach(Action<string>? sink) => Volatile.Write(ref _sink, sink);

    /// <summary>
    /// Writes one line. The sink owns its own failure handling: swallowing here
    /// would hide bugs in the writer instead of the disk errors it expects.
    /// </summary>
    public static void Write(string message) => Volatile.Read(ref _sink)?.Invoke(message);
}
