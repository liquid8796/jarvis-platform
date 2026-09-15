using System.IO;
using System.Text.Json;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Settings;
using JarvisCode.Host;

namespace JarvisCode.App.Services;

/// <summary>
/// Where the text of the two post-tool-result reminders comes from.
///
/// The reference resolves each through three tiers (its <c>zHo</c>): an
/// environment variable, then a <c>client_data</c> map its config endpoint
/// serves per account, then the model's own default. The middle tier has no
/// counterpart here — this build has no such endpoint, and measurement found the
/// reference's server serving no value for either slot to any account — so
/// Settings decides between carrying the reference's first tier alone and adding
/// a local file in the same shape.
///
/// The file is a JSON object of the reference's own shape: one key per slot,
/// whose value is a map from model pattern to reminder text.
/// <code>
/// {
///   "tengu_toasty_thimble": { "claude-opus-5": "…", "claude-fable-*": "…" },
///   "tengu_gentle_parasol": { "*": "…" }
///  }
/// </code>
/// Patterns are matched by <see cref="ToolResultReminders.MatchModelPattern"/>,
/// the reference's own <c>_ce</c>. An empty or blank text disables the reminder
/// for that model, as a blank environment variable does.
/// </summary>
public static class ReminderOverrides
{
    /// <summary>Settings value: the reference's environment tier and nothing else.</summary>
    public const string EnvironmentSource = "environment";

    /// <summary>Settings value: the environment tier, then the local file.</summary>
    public const string FileSource = "file";

    /// <summary>The reference's <c>client_data</c> key for the batching reminder.</summary>
    public const string BatchingKey = "tengu_toasty_thimble";

    /// <summary>The reference's <c>client_data</c> key for the secondary reminder.</summary>
    public const string SecondaryKey = "tengu_gentle_parasol";

    /// <summary>The local stand-in for the server's <c>client_data</c> blob.</summary>
    public static string FilePath => Path.Combine(AppPaths.Root, "reminder-overrides.json");

    /// <summary>
    /// What a file that does not exist yet is written with: the two slots, the
    /// pattern rules, and no text — so opening it shows the shape without
    /// changing a single turn until something is filled in.
    /// </summary>
    public const string StarterFile = """
        {
          // Reminder text by model pattern. A pattern matches when its parts appear in
          // the model id in order; "*" is the last resort, and an exact id wins
          // over any pattern. Blank text switches that reminder off.
          //
          // The environment variables CLAUDE_CODE_TOASTY_THIMBLE and
          // CLAUDE_CODE_GENTLE_PARASOL still come first when they are set.
          //
          // Only a model carrying the mid-conversation system role receives
          // either reminder at all — claude-opus-5 and the fable/mythos family
          // do, claude-opus-4-5 and the haiku models do not.
          "tengu_toasty_thimble": {
            // "claude-opus-5": "First privately list what you need next; …"
          },
          "tengu_gentle_parasol": {
          }
        }

        """;

    /// <summary>Whether the local file tier is in force.</summary>
    public static bool UsesFile(AppSettings settings) =>
        settings.ReminderOverridesEnabled &&
        string.Equals(settings.ReminderOverrideSource, FileSource, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The options for this turn, with the two texts resolved the way the
    /// settings say. With overrides off neither slot carries text, whatever the
    /// environment holds — the model keeps only what it owns.
    /// </summary>
    public static ToolResultReminders.Options Apply(
        ToolResultReminders.Options options,
        AppSettings settings,
        string modelId,
        Func<string>? readFile = null)
    {
        if (!settings.ReminderOverridesEnabled)
        {
            return options with
            {
                BatchingText = ToolResultReminders.ResolveBatchingText(null, options.ModelOwnsText),
                SecondaryText = null,
            };
        }

        if (!UsesFile(settings))
        {
            return options;
        }

        var map = Load(readFile);
        if (map.Count == 0)
        {
            return options;
        }

        return options with
        {
            // The environment stays the first tier, as it is there: the file
            // answers only where the variable said nothing.
            BatchingText = options.BatchingText ?? Text(map, BatchingKey, modelId),
            SecondaryText = options.SecondaryText ?? Text(map, SecondaryKey, modelId),
        };
    }

    /// <summary>The text one slot holds for a model, or null for none.</summary>
    public static string? Text(
        IReadOnlyDictionary<string, Dictionary<string, string?>> map, string slot, string modelId)
    {
        if (!map.TryGetValue(slot, out var byPattern) || byPattern.Count == 0)
        {
            return null;
        }

        var text = ToolResultReminders.MatchModelPattern(byPattern, modelId)?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>
    /// The file's contents, or an empty map when it is absent or unreadable. A
    /// malformed file is not an error a turn should fail on — the reference
    /// warns and carries on when its own <c>client_data</c> value is the wrong
    /// shape (its <c>EBn</c>).
    /// </summary>
    public static IReadOnlyDictionary<string, Dictionary<string, string?>> Load(
        Func<string>? readFile = null)
    {
        try
        {
            var json = readFile is not null
                ? readFile()
                : File.Exists(FilePath) ? File.ReadAllText(FilePath) : null;
            if (string.IsNullOrWhiteSpace(json))
            {
                return EmptyMap;
            }

            return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string?>>>(
                json, Options) ?? EmptyMap;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return EmptyMap;
        }
    }

    private static readonly Dictionary<string, Dictionary<string, string?>> EmptyMap = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
