using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jarvis.Agent.Windows;

/// <summary>
/// Tracks element references produced by the latest browser DOM observation. Material browser
/// mutations invalidate those references so callers cannot silently act on stale accessibility/DOM state.
/// </summary>
public sealed partial class BrowserObservationTracker
{
    private readonly object _sync = new();
    private HashSet<string> _refs = new(StringComparer.Ordinal);
    private long _generation;
    private bool _hasReferenceObservation;

    public long Generation { get { lock (_sync) return _generation; } }

    public void BeforeTool(string toolName, JsonElement arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        foreach (var reference in ReferencedElements(arguments)) EnsureCurrent(reference);
    }

    public void AfterTool(string toolName, JsonElement arguments, string output, bool success)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        if (!success) return;

        if (IsReferenceObservation(toolName))
        {
            var refs = ReferenceRegex().Matches(output ?? string.Empty)
                .Select(match => match.Value)
                .ToHashSet(StringComparer.Ordinal);
            lock (_sync)
            {
                _generation++;
                _refs = refs;
                _hasReferenceObservation = true;
            }
            return;
        }

        if (IsMaterialMutation(toolName, arguments)) Invalidate();
    }

    public void Invalidate()
    {
        lock (_sync)
        {
            _generation++;
            _refs.Clear();
            _hasReferenceObservation = false;
        }
    }

    private void EnsureCurrent(string reference)
    {
        lock (_sync)
        {
            if (!_hasReferenceObservation || !_refs.Contains(reference))
                throw new InvalidOperationException(
                    $"Browser reference '{reference}' is stale or is not part of the current observation generation {_generation}. Refresh with browser.read_page or browser.find before using element refs again.");
        }
    }

    private static IEnumerable<string> ReferencedElements(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in arguments.EnumerateObject())
            {
                if (property.Name is "ref" or "ref_id" && property.Value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(property.Value.GetString()))
                    yield return property.Value.GetString()!;
                else
                    foreach (var nested in ReferencedElements(property.Value)) yield return nested;
            }
            yield break;
        }
        if (arguments.ValueKind == JsonValueKind.Array)
            foreach (var item in arguments.EnumerateArray())
                foreach (var nested in ReferencedElements(item)) yield return nested;
    }

    private static bool IsReferenceObservation(string toolName) =>
        toolName is "browser.read_page" or "browser.find";

    private static bool IsMaterialMutation(string toolName, JsonElement arguments)
    {
        if (toolName is "browser.navigate" or "browser.form_input" or "browser.file_upload" or
            "browser.upload_image" or "browser.javascript_tool" or "browser.resize_window" or
            "browser.tabs_create_mcp" or "browser.tabs_close_mcp" or "browser.select_browser" or
            "browser.browser_batch") return true;

        if (toolName != "browser.computer") return false;
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("action", out var action) ||
            action.ValueKind != JsonValueKind.String) return true;
        return action.GetString() is not ("screenshot" or "zoom" or "wait");
    }

    [GeneratedRegex(@"\bref_[A-Za-z0-9_-]+\b", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceRegex();
}
