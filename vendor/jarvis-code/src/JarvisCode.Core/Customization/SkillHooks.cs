using JarvisCode.Core.Hooks;

namespace JarvisCode.Core.Customization;

/// <summary>
/// Parses a skill's frontmatter "hooks" block — a YAML list of hook entries in
/// the app's hooks.json shape (event, command, toolMatch, timeoutSeconds) —
/// into definitions that join the session once the skill is invoked. A
/// malformed block throws with the reference's "Invalid hooks in skill" error.
/// </summary>
public static class SkillHooks
{
    public static IReadOnlyList<HookDefinition> Parse(string skillName, string hooksBlock)
    {
        var definitions = new List<HookDefinition>();
        string? eventName = null, command = null, toolMatch = null;
        int? timeoutSeconds = null;
        bool inEntry = false;

        void Flush()
        {
            if (!inEntry)
                return;
            var hookEvent = HookRunner.ParseEventName(eventName)
                ?? throw Invalid(skillName, eventName is null
                    ? "an entry is missing its \"event\""
                    : $"unknown event \"{eventName}\"");
            if (string.IsNullOrWhiteSpace(command))
                throw Invalid(skillName, "an entry is missing its \"command\"");
            definitions.Add(new HookDefinition(
                hookEvent, toolMatch, command, HookRunner.ClampTimeout(timeoutSeconds)));
            (eventName, command, toolMatch, timeoutSeconds) = (null, null, null, null);
            inEntry = false;
        }

        foreach (var raw in hooksBlock.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.StartsWith('-'))
            {
                Flush();
                inEntry = true;
                line = line[1..].Trim();
                if (line.Length == 0)
                    continue;
            }
            if (!inEntry)
                throw Invalid(skillName, $"expected a \"- \" list entry, got \"{line}\"");

            int colon = line.IndexOf(':');
            if (colon <= 0)
                throw Invalid(skillName, $"expected \"key: value\", got \"{line}\"");
            var key = Frontmatter.Normalize(line[..colon].Trim());
            var value = Unquote(line[(colon + 1)..].Trim());
            switch (key)
            {
                case "event":
                    eventName = value;
                    break;
                case "command":
                    command = value;
                    break;
                case "toolmatch":
                case "matcher":
                    toolMatch = value;
                    break;
                case "timeoutseconds":
                case "timeout":
                    if (int.TryParse(value, out var parsed))
                        timeoutSeconds = parsed;
                    else
                        throw Invalid(skillName, $"\"{line[..colon].Trim()}\" must be a number, got \"{value}\"");
                    break;
                default:
                    // Unknown keys are tolerated, like the runner's JSON reader.
                    break;
            }
        }
        Flush();
        return definitions;
    }

    private static FormatException Invalid(string skillName, string reason) =>
        new($"Invalid hooks in skill '{skillName}': {reason}");

    private static string Unquote(string value) =>
        value.Length >= 2 &&
        ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;
}
