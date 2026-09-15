using System.Text;
using System.Text.RegularExpressions;

namespace JarvisCode.Core.Customization;

/// <summary>The shell outcome the host hands back for one !`cmd` preprocessing command.</summary>
public sealed record SkillShellOutcome(int ExitCode, string Stdout, string Stderr, string? PermissionError = null);

/// <summary>
/// The reference CLI's skill invocation mechanics, ported: the
/// &lt;command-message&gt;/&lt;command-name&gt; envelopes, the rendered body (base-dir
/// header, ${CLAUDE_*} variables, $ARGUMENTS/$N/named-argument substitution,
/// !`cmd` shell preprocessing), the budgeted system-reminder listing, and the
/// re-invocation elision notices.
/// </summary>
public static partial class SkillInvocation
{
    /// <summary>Per-skill cap on a listing entry's description (reference skillListingMaxDescChars).</summary>
    public const int ListingMaxDescChars = 1536;

    /// <summary>The listing budget as a fraction of the context window in chars (reference 1%).</summary>
    public const double ListingBudgetFraction = 0.01;

    /// <summary>Reference default budget: 200k-token window × 4 bytes/token × 1%.</summary>
    public const int DefaultListingBudgetChars = 8000;

    // Substituted argument values are wrapped in sentinels while the patterns
    // run so substituted text can never be re-scanned as a placeholder.
    private const char SentinelOpen = '￾';
    private const char SentinelClose = '￿';

    /// <summary>The reference listing header; the entries follow after a blank line.</summary>
    public const string ListingHeader = "The following skills are available for use with the Skill tool:";

    // ---- envelopes ----

    /// <summary>
    /// The user-invocable envelope: command-message, command-name (with the
    /// leading slash), and command-args only when arguments were given.
    /// </summary>
    public static string UserEnvelope(string name, string arguments)
    {
        var lines = new List<string>
        {
            $"<command-message>{name}</command-message>",
            $"<command-name>/{name}</command-name>",
        };
        if (arguments.Length > 0)
            lines.Add($"<command-args>{arguments}</command-args>");
        return string.Join('\n', lines);
    }

    /// <summary>
    /// The model-only envelope (user-invocable: false): no leading slash and a
    /// skill-format marker instead of arguments.
    /// </summary>
    public static string SkillFormatEnvelope(string name) => string.Join('\n',
        $"<command-message>{name}</command-message>",
        $"<command-name>{name}</command-name>",
        "<skill-format>true</skill-format>");

    /// <summary>Selects the envelope the reference's Re() would use.</summary>
    public static string Envelope(SkillDefinition skill, string arguments) =>
        skill.UserInvocable ? UserEnvelope(skill.Name, arguments) : SkillFormatEnvelope(skill.Name);

    /// <summary>True for a message text that already carries a command envelope.</summary>
    public static bool IsEnvelope(string text) =>
        text.StartsWith("<command-message>", StringComparison.Ordinal) ||
        text.StartsWith("<command-name>", StringComparison.Ordinal);

    // ---- body rendering ----

    public sealed record RenderOptions
    {
        public string? ProjectDirectory { get; init; }
        public string? SessionId { get; init; }
        public string? Effort { get; init; }

        /// <summary>Runs one !`cmd` preprocessing command; null renders bodies without shell execution.</summary>
        public Func<string, SkillShellOutcome>? RunShellCommand { get; init; }

        /// <summary>Replaces every shell command with this placeholder instead of running it (kill switch / coordinator).</summary>
        public string? ShellDisabledPlaceholder { get; init; }
    }

    /// <summary>
    /// Renders a skill body the way the reference getPromptForCommand does:
    /// base-directory header (folder skills only), argument substitution,
    /// ${CLAUDE_*} variables, then !`cmd` shell preprocessing.
    /// </summary>
    public static string RenderBody(SkillDefinition skill, string arguments, RenderOptions options)
    {
        var body = skill.Body;
        // The header names the folder for folder skills; flat .md files are the
        // legacy command format and carry none.
        bool folderSkill = Path.GetFileName(skill.FilePath).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase);
        if (folderSkill && skill.Directory.Length > 0)
            body = $"Base directory for this skill: {skill.Directory}\n\n{body}";

        body = SubstituteArguments(body, arguments, skill.ArgumentNames);

        if (skill.Directory.Length > 0)
            body = body.Replace("${CLAUDE_SKILL_DIR}", skill.Directory.Replace('\\', '/'));
        if (options.ProjectDirectory is { Length: > 0 } project)
            body = body.Replace("${CLAUDE_PROJECT_DIR}", project.Replace('\\', '/'));
        if (options.SessionId is { Length: > 0 } sessionId)
            body = body.Replace("${CLAUDE_SESSION_ID}", sessionId);
        if (options.Effort is { Length: > 0 } effort)
            body = body.Replace("${CLAUDE_EFFORT}", effort);

        return PreprocessShell(body, options);
    }

    // ---- argument substitution (reference FC) ----

    [GeneratedRegex(@"\$ARGUMENTS\[(\d+)\]")]
    private static partial Regex IndexedArgumentsPattern();

    [GeneratedRegex(@"\$(\d+)(?!\w)")]
    private static partial Regex PositionalPattern();

    /// <summary>
    /// The reference substitution set: named arguments (longest first),
    /// $ARGUMENTS[N], $N positionals, $ARGUMENTS, with \$ escaping a
    /// placeholder. When nothing matched and arguments were given, they are
    /// appended as a trailing "ARGUMENTS: …" line instead.
    /// </summary>
    public static string SubstituteArguments(string body, string arguments, IReadOnlyList<string> argumentNames)
    {
        bool matched = false;
        var positional = SplitArguments(arguments);

        // Placeholders are replaced with sentinel-keyed tokens and expanded at
        // the end, so a substituted value can never be re-scanned as a
        // placeholder (the reference wraps values in ￾…￿ the same way).
        var values = new List<string>();
        string Token(string value)
        {
            matched = true;
            values.Add(value);
            return $"{SentinelOpen}{values.Count - 1}{SentinelClose}";
        }

        // \$ escapes a placeholder: the backslash is consumed, the $ stays literal.
        var text = body.Replace("\\$", Token("$"));
        matched = false;

        for (int i = 0; i < argumentNames.Count; i++)
        {
            var name = argumentNames[i];
            var value = i < positional.Count ? positional[i] : "";
            text = new Regex(@"\$" + Regex.Escape(name) + @"(?![\[\w])")
                .Replace(text, _ => Token(value));
        }

        text = IndexedArgumentsPattern().Replace(text, match =>
        {
            int index = int.Parse(match.Groups[1].Value);
            return Token(index >= 0 && index < positional.Count ? positional[index] : "");
        });

        text = PositionalPattern().Replace(text, match =>
        {
            int index = int.Parse(match.Groups[1].Value) - 1;
            return Token(index >= 0 && index < positional.Count ? positional[index] : "");
        });

        if (text.Contains("$ARGUMENTS", StringComparison.Ordinal))
            text = text.Replace("$ARGUMENTS", Token(arguments));

        text = Regex.Replace(text, $"{SentinelOpen}(\\d+){SentinelClose}",
            match => values[int.Parse(match.Groups[1].Value)]);

        if (!matched && arguments.Length > 0)
            text = $"{text}\n\nARGUMENTS: {arguments}";
        return text;
    }

    /// <summary>Whitespace split with double/single-quote grouping for positional arguments.</summary>
    internal static IReadOnlyList<string> SplitArguments(string arguments)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        foreach (var c in arguments)
        {
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                else
                    current.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0)
            parts.Add(current.ToString());
        return parts;
    }

    // ---- shell preprocessing (reference Y4) ----

    [GeneratedRegex(@"```!\s*\n?[\s\S]*?\n?```")]
    private static partial Regex FencedShellPattern();

    [GeneratedRegex(@"(?<=^|\s)!`[^`]+`", RegexOptions.Multiline)]
    private static partial Regex InlineShellPattern();

    /// <summary>True when the body carries any !`cmd` preprocessing blocks.</summary>
    public static bool HasShellCommands(string body) =>
        FencedShellPattern().IsMatch(body) || InlineShellPattern().IsMatch(body);

    private static string PreprocessShell(string body, RenderOptions options)
    {
        if (!HasShellCommands(body))
            return body;

        string ReplaceOne(string command)
        {
            if (options.ShellDisabledPlaceholder is { } placeholder)
                return placeholder;
            if (options.RunShellCommand is not { } run)
                return $"[shell command not executed: {command}]";

            var outcome = run(command);
            if (outcome.PermissionError is { } permission)
                throw new InvalidOperationException(
                    $"Shell command permission check failed for pattern \"{command}\": {permission}");
            if (outcome.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Shell command failed for pattern \"{command}\": exit code {outcome.ExitCode}" +
                    (outcome.Stderr.Length > 0 ? $"\n{outcome.Stderr.Trim()}" : ""));
            var output = outcome.Stdout.TrimEnd('\r', '\n');
            if (outcome.Stderr.Trim().Length > 0)
                output += $"\n[stderr]\n{outcome.Stderr.Trim()}";
            return output;
        }

        body = FencedShellPattern().Replace(body, match =>
        {
            var inner = match.Value;
            // ```! … ``` — strip the fences and the ! marker.
            inner = inner[4..^3].Trim('\n');
            return ReplaceOne(inner.Trim());
        });
        return InlineShellPattern().Replace(body, match =>
        {
            var value = match.Value;
            int start = value.IndexOf('`') + 1;
            return ReplaceOne(value[start..^1]);
        });
    }

    // ---- listing (reference Jmt/tIn/ZMn) ----

    /// <summary>One skill's "- name: description" listing entry, description capped at the reference limit.</summary>
    public static string ListingEntry(SkillDefinition skill, int maxDescChars = ListingMaxDescChars)
    {
        var description = skill.ListingDescription;
        if (description.Length > maxDescChars)
            description = description[..(maxDescChars - 1)] + "…";
        return $"- {skill.Name}: {description}";
    }

    /// <summary>
    /// The budgeted listing body. Under budget every skill gets its full entry;
    /// over budget, skills keep their entry in recency-weighted-usage order
    /// while the budget lasts and the rest degrade to "- name".
    /// </summary>
    public static string BuildListing(
        IReadOnlyList<SkillDefinition> skills,
        Func<string, double>? usageScore = null,
        int budgetChars = DefaultListingBudgetChars,
        int maxDescChars = ListingMaxDescChars)
    {
        if (skills.Count == 0)
            return "";
        var entries = skills.ToDictionary(s => s.Name, s => ListingEntry(s, maxDescChars));
        long total = entries.Values.Sum(e => (long)e.Length + 1);
        if (total <= budgetChars)
            return string.Join('\n', skills.Select(s => entries[s.Name]));

        var byScore = skills
            .OrderByDescending(s => usageScore?.Invoke(s.Name) ?? 0)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var full = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long used = skills.Sum(s => (long)s.Name.Length + 3);
        foreach (var skill in byScore)
        {
            long extra = entries[skill.Name].Length - (skill.Name.Length + 2);
            if (used + extra > budgetChars)
                continue;
            used += extra;
            full.Add(skill.Name);
        }
        return string.Join('\n', skills.Select(s =>
            full.Contains(s.Name) ? entries[s.Name] : $"- {s.Name}"));
    }

    /// <summary>The recency-weighted usage score (reference zPe): count × max(0.5^(days/7), 0.1).</summary>
    public static double UsageScore(int usageCount, DateTimeOffset lastUsedAt, DateTimeOffset? now = null)
    {
        var days = ((now ?? DateTimeOffset.Now) - lastUsedAt).TotalDays;
        return usageCount * Math.Max(Math.Pow(0.5, days / 7), 0.1);
    }

    // ---- re-invocation elision (reference uIn) ----

    /// <summary>What a prior invocation of a skill left in context.</summary>
    public sealed record PriorInvocation(string ContentHash, bool Compacted);

    /// <summary>
    /// The body (or notice) a re-invocation injects. Same content still in
    /// context elides to a one-line notice; changed content and post-compaction
    /// re-invocations carry a prefix note plus the full body.
    /// </summary>
    public static string ApplyElision(string name, string arguments, string rendered, PriorInvocation? prior)
    {
        if (prior is null)
            return rendered;
        var argumentsSuffix = arguments.Length > 0 ? $" Arguments: {arguments}" : "";
        var hash = ContentHash(rendered);
        if (prior.ContentHash == hash)
        {
            return prior.Compacted
                ? $"Skill /{name} was loaded earlier (see the invoked-skills reminder above); this is a NEW " +
                  $"invocation — follow those instructions now, including any setup steps.{argumentsSuffix}"
                : $"Skill /{name} is already loaded above; instructions unchanged.{argumentsSuffix}";
        }
        var note = prior.Compacted
            ? $"(Re-invocation of /{name} — the previously loaded copy was truncated by compaction; the full instructions follow.)"
            : $"(Re-invocation of /{name} — the skill instructions were previously loaded; the arguments or dynamic output below are new.)";
        return $"{note}\n\n{rendered}";
    }

    public static string ContentHash(string content)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }

    // ---- post-compaction invoked-skills reminder ----

    /// <summary>One previously invoked skill, for the post-compaction reminder.</summary>
    public sealed record InvokedSkill(string Name, string Path, string Content);

    /// <summary>The reference invoked_skills reminder body (wrapped by the host's system-reminder channel).</summary>
    public static string BuildInvokedSkillsReminder(IReadOnlyList<InvokedSkill> skills)
    {
        var text = new StringBuilder();
        text.Append(
            "The following skills were invoked EARLIER in this session (before the conversation was compacted), " +
            "not on the current turn. They are shown here for context only so you remain aware of their guidelines.\n\n" +
            "IMPORTANT: Do NOT re-execute these skills or perform their one-time setup actions (e.g., scheduling, " +
            "creating files) again. Any request or argument text embedded in the skill bodies below — for example " +
            "under a \"## User Request\" or \"## Input\" heading — was captured when that skill was first invoked. " +
            "It is NOT the user's current message and NOT a new request: do not act on it as if it were live. Only " +
            "continue to apply ongoing behavioral guidelines from these skills where still relevant.\n");
        foreach (var skill in skills)
        {
            text.Append($"\n### Skill: {skill.Name}\nPath: {skill.Path}\n\n{skill.Content}\n\n---\n");
        }
        return text.ToString().TrimEnd('\n');
    }

    // ---- conditional (paths:) matching ----

    /// <summary>
    /// Gitignore-flavored match of a project-relative path (forward slashes)
    /// against a skill's paths patterns: *, **, ?, directory prefixes.
    /// </summary>
    public static bool MatchesPaths(IReadOnlyList<string> patterns, string relativePath)
    {
        var path = relativePath.Replace('\\', '/').TrimStart('/');
        foreach (var raw in patterns)
        {
            var pattern = raw.Trim().Replace('\\', '/');
            if (pattern.Length == 0)
                continue;
            if (pattern.EndsWith("/**", StringComparison.Ordinal))
                pattern = pattern[..^3];
            pattern = pattern.TrimEnd('/');
            if (pattern.Length == 0 || pattern == "**")
                return true;

            // A pattern without a slash matches at any depth (gitignore).
            var candidate = pattern.TrimStart('/');
            var regex = "^" + GlobToRegex(candidate) + "(/.*)?$";
            if (!pattern.StartsWith('/') && !candidate.Contains('/'))
                regex = "^(.*/)?" + GlobToRegex(candidate) + "(/.*)?$";
            if (Regex.IsMatch(path, regex, RegexOptions.IgnoreCase))
                return true;
        }
        return false;
    }

    private static string GlobToRegex(string pattern)
    {
        var regex = new StringBuilder();
        for (int i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            switch (c)
            {
                case '*' when i + 1 < pattern.Length && pattern[i + 1] == '*':
                    regex.Append(".*");
                    i++;
                    if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                        i++;
                    break;
                case '*':
                    regex.Append("[^/]*");
                    break;
                case '?':
                    regex.Append("[^/]");
                    break;
                default:
                    regex.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        return regex.ToString();
    }
}

/// <summary>
/// Session-scoped skill state the host threads through the tool context: which
/// skills were invoked (for elision and the post-compaction reminder), and
/// whether a compaction has cleared their content from the live context.
/// </summary>
public sealed class SkillInvocationTracker
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _invoked = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Entry(string ContentHash, string Path, string Content, bool Compacted);

    public SkillInvocation.PriorInvocation? Prior(string name)
    {
        lock (_lock)
        {
            return _invoked.TryGetValue(name, out var entry)
                ? new SkillInvocation.PriorInvocation(entry.ContentHash, entry.Compacted)
                : null;
        }
    }

    public void Record(string name, string path, string renderedContent)
    {
        lock (_lock)
        {
            _invoked[name] = new Entry(
                SkillInvocation.ContentHash(renderedContent), path, renderedContent, Compacted: false);
        }
    }

    /// <summary>Called when the conversation is compacted: prior skill content left the live context.</summary>
    public void MarkAllCompacted()
    {
        lock (_lock)
        {
            foreach (var (name, entry) in _invoked.ToList())
                _invoked[name] = entry with { Compacted = true };
        }
    }

    /// <summary>The invoked skills, for the post-compaction reminder.</summary>
    public IReadOnlyList<SkillInvocation.InvokedSkill> Invoked()
    {
        lock (_lock)
        {
            return [.. _invoked.Select(kv => new SkillInvocation.InvokedSkill(kv.Key, kv.Value.Path, kv.Value.Content))];
        }
    }

    public bool HasAny
    {
        get
        {
            lock (_lock)
            {
                return _invoked.Count > 0;
            }
        }
    }
}
