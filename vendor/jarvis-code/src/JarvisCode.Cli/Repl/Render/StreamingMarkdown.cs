using System.Text;

namespace JarvisCode.Cli.Repl.Render;

/// <summary>
/// Renders a streaming answer into the scrollback a block at a time. A terminal
/// cannot restyle what it has already printed, so a block is held back until it
/// is closed — a paragraph until the blank line after it, a fenced block until
/// its closing fence — and only then rendered and emitted. That is the same
/// shape as the reference's own rule for a streaming table (hold the construct
/// until it is whole) applied to every block, rather than printing markdown
/// syntax that would still be visible once the answer settled.
/// </summary>
internal sealed class StreamingMarkdownSink(MarkdownTerminal markdown)
{
    private readonly StringBuilder _pending = new();
    private bool _inFence;
    private string _fenceMarker = "";

    /// <summary>Text the sink has decided to emit, in order.</summary>
    public List<string> Emitted { get; } = [];

    /// <summary>Whether anything at all has been emitted, so the caller can space the answer.</summary>
    public bool HasEmitted { get; private set; }

    public void Append(string delta)
    {
        _pending.Append(delta);
        Drain();
    }

    /// <summary>Renders whatever is left, which is what a completed message does.</summary>
    public void Flush()
    {
        var rest = _pending.ToString();
        _pending.Clear();
        if (rest.Trim().Length == 0)
        {
            return;
        }

        Emit(rest);
    }

    private void Drain()
    {
        while (true)
        {
            var text = _pending.ToString();
            int cut = NextBlockEnd(text);
            if (cut < 0)
            {
                return;
            }

            var block = text[..cut];
            _pending.Remove(0, cut);
            if (block.Trim().Length > 0)
            {
                Emit(block);
            }
        }
    }

    /// <summary>
    /// The end of the first complete block in the buffer, or -1 while none is
    /// closed. A fence opened in the buffer is complete only once its closing
    /// marker has arrived, so a half-written fence never reaches the screen.
    /// </summary>
    internal int NextBlockEnd(string text)
    {
        int index = 0;
        bool inFence = _inFence;
        var marker = _fenceMarker;
        while (index < text.Length)
        {
            int lineEnd = text.IndexOf('\n', index);
            if (lineEnd < 0)
            {
                return -1;
            }

            var line = text[index..lineEnd];
            var trimmed = line.TrimStart();
            if (inFence)
            {
                if (marker.Length > 0 && trimmed.StartsWith(marker, StringComparison.Ordinal))
                {
                    inFence = false;
                    marker = "";
                    _inFence = false;
                    _fenceMarker = "";
                    return lineEnd + 1;
                }
            }
            else if (trimmed.StartsWith("```", StringComparison.Ordinal) ||
                     trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = true;
                marker = trimmed[..3];
                _inFence = true;
                _fenceMarker = marker;
            }
            else if (line.Trim().Length == 0)
            {
                return lineEnd + 1;
            }

            index = lineEnd + 1;
        }

        return -1;
    }

    private void Emit(string block)
    {
        var rendered = markdown.Render(block);
        if (rendered.Length == 0)
        {
            return;
        }

        Emitted.Add(rendered);
        HasEmitted = true;
    }

    /// <summary>Takes whatever has been emitted since the last call.</summary>
    public IReadOnlyList<string> Take()
    {
        if (Emitted.Count == 0)
        {
            return [];
        }

        var taken = Emitted.ToArray();
        Emitted.Clear();
        return taken;
    }
}
