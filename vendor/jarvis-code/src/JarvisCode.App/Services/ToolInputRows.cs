using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace JarvisCode.App.Services;

/// <summary>How one argument's value is drawn.</summary>
public enum ToolInputValueKind
{
    /// <summary>An ordinary string: the value as prose, in the body's own face.</summary>
    Plain,

    /// <summary>A path: a code chip that opens the file.</summary>
    FileRef,

    /// <summary>Something written rather than said — a command, a pattern, code.</summary>
    Code,
}

/// <summary>One argument of a tool call, as the expanded row prints it.</summary>
public readonly record struct ToolInputRow(string Key, string Value, ToolInputValueKind Kind)
{
    public string Label => $"{Key}:";
    public bool IsFileRef => Kind == ToolInputValueKind.FileRef;
    public bool IsCode => Kind == ToolInputValueKind.Code;
    public bool IsPlain => Kind == ToolInputValueKind.Plain;
}

/// <summary>
/// A tool call's arguments the way the reference desktop's expanded row prints
/// them (desktop 1.44121.2.0, ion-dist <c>cd5a31703-DPCARDPv.js</c>: <c>KC</c>
/// draws the list, <c>qC</c> decides a value's face, <c>hC</c> renders a
/// non-string and <c>gC</c> builds the copy text).
///
/// It is a list of "<c>key:</c> value" lines rather than one JSON blob, which is
/// the difference a reader notices: a path arrives as something to click and a
/// command arrives in the code face, while a description arrives as the sentence
/// it is instead of a quoted, escaped JSON string.
/// </summary>
public static class ToolInputRows
{
    /// <summary>The reference's <c>UC</c>: the two keys that name a file.</summary>
    private static readonly HashSet<string> PathKeys = new(StringComparer.Ordinal)
    {
        "file_path", "notebook_path",
    };

    /// <summary>The reference's <c>WC</c>: the keys whose value is written, not said.</summary>
    private static readonly HashSet<string> CodeKeys = new(StringComparer.Ordinal)
    {
        "command", "cmd", "script", "shell", "code", "pattern", "regex", "glob",
    };

    /// <summary>The argument rows, in the order the call declared them.</summary>
    public static IReadOnlyList<ToolInputRow> Build(JsonObject? args)
    {
        if (args is null || args.Count == 0)
        {
            return [];
        }

        var rows = new List<ToolInputRow>(args.Count);
        foreach (var (key, node) in args)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var text))
            {
                rows.Add(new ToolInputRow(
                    key,
                    text,
                    PathKeys.Contains(key) ? ToolInputValueKind.FileRef
                        : CodeKeys.Contains(key) ? ToolInputValueKind.Code
                        : ToolInputValueKind.Plain));
            }
            else
            {
                rows.Add(new ToolInputRow(key, Render(node), ToolInputValueKind.Code));
            }
        }

        return rows;
    }

    /// <summary>The reference's <c>hC</c>: a string as itself, anything else as JSON.</summary>
    private static string Render(JsonNode? node)
    {
        if (node is null)
        {
            return "null";
        }

        try
        {
            return node.ToJsonString();
        }
        catch (Exception)
        {
            return node.ToString();
        }
    }

    /// <summary>The reference's <c>gC</c>: the argument list as one block of text.</summary>
    public static string CopyText(JsonObject? args) =>
        string.Join("\n", Build(args).Select(row => $"{row.Key}: {row.Value}"));

    /// <summary>The reference's <c>yC</c>: two blocks, blank-line separated, empties dropped.</summary>
    public static string Join(string? first, string? second)
    {
        var builder = new StringBuilder();
        foreach (var part in new[] { first, second })
        {
            if (string.IsNullOrEmpty(part))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append("\n\n");
            }

            builder.Append(part);
        }

        return builder.ToString();
    }
}
