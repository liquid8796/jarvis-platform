using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using JarvisCode.Cli.Repl.Terminal;

namespace JarvisCode.Cli.Repl.Keys;

/// <summary>One validation issue the loader reports, in the reference's shape.</summary>
internal sealed record KeybindingIssue(
    string Type, string Severity, string Message, string? Suggestion = null,
    string? Key = null, string? Context = null, string? Action = null)
{
    /// <summary>The reference's log line: <c>[keybindings] [{severity}] {message} — {suggestion}</c>.</summary>
    public string LogLine => $"[keybindings] [{Severity}] {Message}{(Suggestion is null ? "" : " — " + Suggestion)}";
}

internal sealed record KeybindingsLoad(KeyMap Map, IReadOnlyList<KeybindingIssue> Warnings, int UserBindingCount);

/// <summary>
/// The reference's <c>keybindings.json</c> loader (CLI 2.1.257, its <c>Q4e</c>
/// family): <c>{"bindings": [{"context": "...", "bindings": {"chord": "action"|null}}]}</c>
/// appended after the defaults, with its validation — the shape errors, unknown
/// contexts, empty key parts, unknown actions with a Levenshtein suggestion,
/// <c>command:</c> bindings outside Chat, duplicates inside a context (both the
/// parsed scan and the raw-text one its <c>_Be</c> runs), and the reserved keys
/// a terminal cannot deliver — each carrying the reference's own sentence.
/// </summary>
internal static partial class KeybindingsFile
{
    public const string MissingBindingsArray = "keybindings.json must have a \"bindings\" array";
    public const string MissingBindingsArraySuggestion = "Use format: { \"bindings\": [ ... ] }";
    public const string BindingsNotArray = "\"bindings\" must be an array";
    public const string BindingsNotArraySuggestion = "Set \"bindings\" to an array of keybinding blocks";
    public const string InvalidBlockStructure = "keybindings.json contains invalid block structure";
    public const string InvalidBlockStructureSuggestion =
        "Each block must have \"context\" (string) and \"bindings\" (object mapping keys to a string action or null)";
    public const string MustContainArray = "keybindings.json must contain an array";
    public const string MustContainArraySuggestion = "Wrap your bindings in [ ]";
    public const string EmptyPartSuggestion = "Remove extra \"+\" characters";
    public const string DuplicateKeySuggestion =
        "This key appears multiple times in the same context. JSON uses the last value, earlier values are ignored.";
    public const string CommandContextSuggestion = "Move this binding to a block with \"context\": \"Chat\"";
    public const string BindingIgnoredSuffix = " — this binding is ignored";

    /// <summary>The reference's <c>command:</c> shape.</summary>
    [GeneratedRegex(@"^command:[a-zA-Z0-9:\-_]+$")]
    private static partial Regex CommandBinding();

    /// <summary>The reference's <c>_Be</c>: it re-scans the raw text for repeated keys inside one block.</summary>
    [GeneratedRegex("\"bindings\"\\s*:\\s*\\{([^{}]*(?:\\{[^{}]*\\}[^{}]*)*)\\}")]
    private static partial Regex RawBindingsBlock();

    [GeneratedRegex("\"([^\"]+)\"\\s*:")]
    private static partial Regex RawKey();

    [GeneratedRegex("\"context\"\\s*:\\s*\"([^\"]+)\"[^{]*$")]
    private static partial Regex RawContextBefore();

    public static KeybindingsLoad Load(string path)
    {
        if (!File.Exists(path))
        {
            return new KeybindingsLoad(KeyMap.Default, [], 0);
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new KeybindingsLoad(KeyMap.Default,
                [new KeybindingIssue("parse_error", "error", $"Failed to parse keybindings.json: {ex.Message}")], 0);
        }

        return Parse(text);
    }

    /// <summary>The loader over file text, for tests.</summary>
    public static KeybindingsLoad Parse(string text, IReadOnlyList<KeyBinding>? defaults = null)
    {
        defaults ??= KeyBindings.Defaults;
        JsonNode? root;
        try
        {
            // JSON.parse keeps the last of two identical keys; System.Text.Json's
            // JsonObject throws on the second. The document is therefore rebuilt
            // rather than materialized, so a file with a duplicate key still
            // loads and still gets the reference's duplicate warning.
            root = ParseLastWins(text);
        }
        catch (JsonException ex)
        {
            return new KeybindingsLoad(new KeyMap(defaults),
                [new KeybindingIssue("parse_error", "error", $"Failed to parse keybindings.json: {ex.Message}")], 0);
        }

        if (root is not JsonObject obj || !obj.ContainsKey("bindings"))
        {
            return new KeybindingsLoad(new KeyMap(defaults),
                [new KeybindingIssue("parse_error", "error", MissingBindingsArray, MissingBindingsArraySuggestion)], 0);
        }

        var blocksNode = obj["bindings"];
        if (blocksNode is not JsonArray blocks)
        {
            return new KeybindingsLoad(new KeyMap(defaults),
                [new KeybindingIssue("parse_error", "error", BindingsNotArray, BindingsNotArraySuggestion)], 0);
        }

        if (!blocks.All(IsBlockShaped))
        {
            return new KeybindingsLoad(new KeyMap(defaults),
                [new KeybindingIssue("parse_error", "error", InvalidBlockStructure, InvalidBlockStructureSuggestion)], 0);
        }

        // The reference's TBe: keep the blocks' bindings whose action is null
        // (an unbind) or a valid action.
        var user = new List<KeyBinding>();
        foreach (var block in blocks.OfType<JsonObject>())
        {
            var context = block["context"]!.GetValue<string>();
            foreach (var (chord, actionNode) in (JsonObject)block["bindings"]!)
            {
                string? action = actionNode?.GetValue<string>();
                if (action is not null && !KeyBindings.IsValidAction(action))
                {
                    continue;
                }

                user.Add(new KeyBinding(
                    chord.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(KeyChords.NormalizeChord).ToArray(),
                    action, context));
            }
        }

        var all = new List<KeyBinding>(defaults);
        all.AddRange(user);
        var warnings = Validate(text, blocks, UserChords(blocks));
        return new KeybindingsLoad(new KeyMap(all), warnings, user.Count);
    }

    private static bool IsBlockShaped(JsonNode? node)
    {
        if (node is not JsonObject block)
        {
            return false;
        }

        if (block["context"] is not JsonValue ctx || !ctx.TryGetValue<string>(out _))
        {
            return false;
        }

        if (block["bindings"] is not JsonObject bindings)
        {
            return false;
        }

        foreach (var (_, value) in bindings)
        {
            if (value is not null && !(value is JsonValue v && v.TryGetValue<string>(out _)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The reference's <c>bBe</c> plus its <c>_Be</c>: the per-block checks, the
    /// raw-text duplicate scan, the parsed duplicate scan, then the reserved
    /// keys — deduplicated by type, key and context.
    /// </summary>
    internal static IReadOnlyList<KeybindingIssue> Validate(
        string rawText, JsonArray blocks, IReadOnlyList<KeyBinding> userBindings)
    {
        var issues = new List<KeybindingIssue>();
        issues.AddRange(RawDuplicates(rawText));
        int index = 0;
        foreach (var node in blocks)
        {
            issues.AddRange(ValidateBlock(node, index));
            index++;
        }

        issues.AddRange(Duplicates(blocks));
        issues.AddRange(ReservedUses(userBindings));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        return issues.Where(issue => seen.Add($"{issue.Type}:{issue.Key}:{issue.Context}")).ToList();
    }

    /// <summary>The reference's <c>vNo</c>: the user blocks flattened into chord/action/context rows.</summary>
    private static IReadOnlyList<KeyBinding> UserChords(JsonArray blocks)
    {
        var rows = new List<KeyBinding>();
        foreach (var block in blocks.OfType<JsonObject>())
        {
            var context = block["context"]?.GetValue<string>() ?? "";
            if (block["bindings"] is not JsonObject bindings)
            {
                continue;
            }

            foreach (var (chord, actionNode) in bindings)
            {
                rows.Add(new KeyBinding(
                    chord.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToArray(),
                    actionNode is JsonValue v && v.TryGetValue<string>(out var action) ? action : null,
                    context));
            }
        }

        return rows;
    }

    /// <summary>JSON.parse's duplicate-key rule: the last value for a name wins.</summary>
    private static JsonNode? ParseLastWins(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        return Convert(document.RootElement);
    }

    private static JsonNode? Convert(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var property in element.EnumerateObject())
                {
                    obj[property.Name] = Convert(property.Value);
                }

                return obj;
            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in element.EnumerateArray())
                {
                    array.Add(Convert(item));
                }

                return array;
            case JsonValueKind.Null:
                return null;
            default:
                return JsonValue.Create(element.Clone());
        }
    }

    /// <summary>The reference's <c>bNo</c>.</summary>
    private static IEnumerable<KeybindingIssue> ValidateBlock(JsonNode? node, int index)
    {
        if (node is not JsonObject block)
        {
            yield return new KeybindingIssue("parse_error", "error", $"Keybinding block {index + 1} is not an object");
            yield break;
        }

        string? context = null;
        if (block["context"] is not JsonValue ctxValue || !ctxValue.TryGetValue<string>(out var ctx))
        {
            yield return new KeybindingIssue(
                "parse_error", "error", $"Keybinding block {index + 1} missing \"context\" field");
        }
        else if (!KeyBindings.Contexts.Contains(ctx))
        {
            yield return new KeybindingIssue("invalid_context", "error", $"Unknown context \"{ctx}\"",
                $"Valid contexts: {string.Join(", ", KeyBindings.Contexts)}", Context: ctx);
        }
        else
        {
            context = ctx;
        }

        if (block["bindings"] is not JsonObject bindings)
        {
            yield return new KeybindingIssue(
                "parse_error", "error", $"Keybinding block {index + 1} missing \"bindings\" field");
            yield break;
        }

        foreach (var (chord, actionNode) in bindings)
        {
            if (KeyChords.EmptyPartError(chord) is { } empty)
            {
                yield return new KeybindingIssue(
                    "parse_error", "error", empty, EmptyPartSuggestion, Key: chord, Context: context);
            }

            if (actionNode is not null && !(actionNode is JsonValue av && av.TryGetValue<string>(out _)))
            {
                yield return new KeybindingIssue("invalid_action", "error",
                    $"Invalid action for \"{chord}\": must be a string or null", Key: chord, Context: context);
                continue;
            }

            if (actionNode?.GetValue<string>() is not { } action)
            {
                continue;
            }

            if (action.StartsWith("command:", StringComparison.Ordinal))
            {
                if (!CommandBinding().IsMatch(action))
                {
                    yield return new KeybindingIssue("invalid_action", "warning",
                        $"Invalid command binding \"{action}\" for \"{chord}\": command name may only contain " +
                        "alphanumeric characters, colons, hyphens, and underscores",
                        Key: chord, Context: context, Action: action);
                }

                if (context is { } named && named != "Chat")
                {
                    yield return new KeybindingIssue("invalid_action", "warning",
                        $"Command binding \"{action}\" must be in \"Chat\" context, not \"{named}\"",
                        CommandContextSuggestion, Key: chord, Context: context, Action: action);
                }
            }
            else if (!KeyBindings.IsValidAction(action))
            {
                var where = context is null ? "" : $" in {context}";
                yield return new KeybindingIssue("invalid_action", "error",
                    $"Unknown action \"{action}\" for \"{chord}\"{where}{BindingIgnoredSuffix}",
                    SuggestAction(action), Key: chord, Context: context, Action: action);
            }
            else if (action == "voice:pushToTalk" &&
                     KeyChords.NormalizeSpelling(chord).Split(' ')[0] is { Length: 1 } bare &&
                     bare[0] is >= 'a' and <= 'z')
            {
                yield return new KeybindingIssue("invalid_action", "warning",
                    $"Binding \"{chord}\" to voice:pushToTalk prints into the input during warmup; " +
                    "use space or a modifier combo like meta+k",
                    Key: chord, Context: context, Action: action);
            }
        }
    }

    /// <summary>The reference's <c>SNo</c>: the nearest action, then the namespace lists.</summary>
    internal static string SuggestAction(string action)
    {
        string? nearest = null;
        int best = int.MaxValue;
        foreach (var candidate in KeyBindings.Actions)
        {
            int distance = Levenshtein(action.ToLowerInvariant(), candidate.ToLowerInvariant());
            if (distance < best)
            {
                best = distance;
                nearest = candidate;
            }
        }

        if (nearest is not null && best <= 2)
        {
            return $"Did you mean \"{nearest}\"?";
        }

        var prefix = action.Split(':')[0];
        var sameNamespace = KeyBindings.Actions
            .Where(a => a.StartsWith(prefix + ":", StringComparison.Ordinal)).ToList();
        if (sameNamespace.Count > 0)
        {
            return $"Valid \"{prefix}:\" actions: {string.Join(", ", sameNamespace)}";
        }

        var namespaces = new List<string>();
        foreach (var a in KeyBindings.Actions)
        {
            var head = a.Split(':')[0];
            if (!namespaces.Contains(head, StringComparer.Ordinal))
            {
                namespaces.Add(head);
            }
        }

        return $"Valid action namespaces: {string.Join(", ", namespaces.Select(n => n + ":"))}";
    }

    internal static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        for (int i = 0; i <= b.Length; i++)
        {
            previous[i] = i;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
            }

            previous = current;
        }

        return previous[b.Length];
    }

    /// <summary>The reference's <c>_Be</c>: the same key spelled twice inside one raw block.</summary>
    private static IEnumerable<KeybindingIssue> RawDuplicates(string rawText)
    {
        foreach (Match block in RawBindingsBlock().Matches(rawText))
        {
            var body = block.Groups[1].Value;
            if (body.Length == 0)
            {
                continue;
            }

            var before = rawText[..block.Index];
            var context = RawContextBefore().Match(before) is { Success: true } m ? m.Groups[1].Value : "unknown";
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Match key in RawKey().Matches(body))
            {
                var name = key.Groups[1].Value;
                counts[name] = counts.TryGetValue(name, out var n) ? n + 1 : 1;
                if (counts[name] == 2)
                {
                    yield return new KeybindingIssue("duplicate", "warning",
                        $"Duplicate key \"{name}\" in {context} bindings", DuplicateKeySuggestion,
                        Key: name, Context: context);
                }
            }
        }
    }

    /// <summary>The reference's <c>ENo</c>: the same normalized chord bound twice in one context.</summary>
    private static IEnumerable<KeybindingIssue> Duplicates(JsonArray blocks)
    {
        var byContext = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var block in blocks.OfType<JsonObject>())
        {
            var context = block["context"]?.GetValue<string>() ?? "";
            if (!byContext.TryGetValue(context, out var seen))
            {
                seen = byContext[context] = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            if (block["bindings"] is not JsonObject bindings)
            {
                continue;
            }

            foreach (var (chord, actionNode) in bindings)
            {
                var normalized = KeyChords.NormalizeSpelling(chord);
                var action = actionNode is JsonValue v && v.TryGetValue<string>(out var a) ? a : "null (unbind)";
                if (seen.TryGetValue(normalized, out var previous) && previous != action)
                {
                    yield return new KeybindingIssue("duplicate", "warning",
                        $"Duplicate binding \"{chord}\" in {context} context",
                        $"Previously bound to \"{previous}\". Only the last binding will be used.",
                        Key: chord, Context: context, Action: action);
                }

                seen[normalized] = actionNode is null ? "null" : action;
            }
        }
    }

    /// <summary>The reference's <c>TNo</c>: any binding whose chord is a key the terminal reserves.</summary>
    private static IEnumerable<KeybindingIssue> ReservedUses(IReadOnlyList<KeyBinding> all)
    {
        foreach (var binding in all)
        {
            var spelling = binding.Spelling;
            var normalized = KeyChords.NormalizeSpelling(spelling);
            foreach (var reserved in KeyBindings.Reserved)
            {
                if (KeyChords.NormalizeSpelling(reserved.Key) == normalized)
                {
                    yield return new KeybindingIssue("reserved", reserved.Severity,
                        $"\"{spelling}\" may not work: {reserved.Reason}",
                        Key: spelling, Context: binding.Context, Action: binding.Action);
                }
            }
        }
    }

    /// <summary>
    /// The file the reference writes when none exists (its <c>ds</c>): the two
    /// schema pointers and one example binding, so the shape is visible.
    /// </summary>
    public const string Template =
        """
        {
          "$schema": "https://www.schemastore.org/claude-code-keybindings.json",
          "$docs": "https://code.claude.com/docs/en/keybindings",
          "bindings": [
            {
              "context": "Chat",
              "bindings": {
                "ctrl+e": "chat:externalEditor"
              }
            }
          ]
        }
        """;
}
