namespace JarvisCode.Cli;

/// <summary>
/// One option a command accepts, commander-style. <see cref="ValuePlaceholder"/>
/// null means a boolean flag; "[value]"-style optional values consume the next
/// token only when it does not look like another option; variadic options keep
/// consuming tokens until the next option (and also split a single token on
/// commas, matching the reference's "comma or space-separated" lists).
/// </summary>
internal sealed record OptionSpec(
    string Long,
    string? Short = null,
    string? ValuePlaceholder = null,
    bool Variadic = false,
    string[]? Choices = null,
    string[]? Aliases = null,
    bool OptionalValue = false,
    /// <summary>Extra check on a value; returns the reason it is invalid, or null.</summary>
    Func<string, string?>? Validate = null,
    /// <summary>True for an option the reference parses but never lists in --help.</summary>
    bool Hidden = false,
    /// <summary>Compatibility values accepted without expanding the documented choice/error list.</summary>
    string[]? HiddenChoices = null)
{
    public string Key => Long.TrimStart('-');

    public bool Matches(string token) =>
        token == Long || token == Short || Aliases?.Contains(token) == true;

    /// <summary>The display name used in commander's error strings.</summary>
    public string ErrorName => ValuePlaceholder is null
        ? Long
        : Aliases is { Length: > 0 }
            ? $"{Long}, {string.Join(", ", Aliases)} {ValuePlaceholder}"
            : $"{Long} {ValuePlaceholder}";
}

/// <summary>Parse result: flags/values by option key, positionals, or one error.</summary>
internal sealed class ParsedArgs
{
    public Dictionary<string, List<string>> Values { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Flags { get; } = new(StringComparer.Ordinal);
    public List<string> OrderedOptions { get; } = [];
    public List<string> Positionals { get; } = [];
    public string? Error { get; set; }
    public bool HelpRequested { get; set; }
    public bool VersionRequested { get; set; }

    public bool Has(string key) => Flags.Contains(key) || Values.ContainsKey(key);

    public string? Value(string key) =>
        Values.TryGetValue(key, out var list) && list.Count > 0 ? list[^1] : null;

    public IReadOnlyList<string> ValueList(string key) =>
        Values.TryGetValue(key, out var list) ? list : [];
}

/// <summary>
/// A commander-compatible argument parser: long/short options, "--opt=value",
/// optional values, variadic lists, choice validation, and the reference CLI's
/// exact error wording ("error: unknown option '--x'", "argument missing",
/// "argument 'x' is invalid. Allowed choices are a, b.").
/// </summary>
internal static class CommandLine
{
    public static ParsedArgs Parse(IReadOnlyList<string> args, IReadOnlyList<OptionSpec> specs)
    {
        var result = new ParsedArgs();
        bool optionsEnded = false;
        for (int i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (optionsEnded || token.Length == 0 || token[0] != '-' || token == "-")
            {
                result.Positionals.Add(token);
                continue;
            }

            if (token == "--")
            {
                optionsEnded = true;
                continue;
            }

            if (token is "-h" or "--help")
            {
                result.HelpRequested = true;
                return result;
            }

            if (token is "-v" or "--version")
            {
                result.VersionRequested = true;
                return result;
            }

            string? inline = null;
            var name = token;
            int eq = token.IndexOf('=');
            if (eq > 0 && token.StartsWith("--", StringComparison.Ordinal))
            {
                name = token[..eq];
                inline = token[(eq + 1)..];
            }

            var spec = specs.FirstOrDefault(s => s.Matches(name));
            if (spec is null)
            {
                // Errors do not stop the scan: a later -h/--help or -v/--version
                // still wins, exactly as commander does ("--bogus --help" prints
                // help and exits 0). Only the first error is reported.
                result.Error ??= $"error: unknown option '{name}'";
                continue;
            }

            if (spec.ValuePlaceholder is null)
            {
                result.OrderedOptions.Add(spec.Key);
                result.Flags.Add(spec.Key);
                continue;
            }

            var values = new List<string>();
            if (inline is not null)
            {
                values.Add(inline);
            }
            else if (spec.Variadic)
            {
                while (i + 1 < args.Count && !LooksLikeOption(args[i + 1]))
                {
                    values.Add(args[++i]);
                }
            }
            else if (i + 1 < args.Count && !(spec.OptionalValue && LooksLikeOption(args[i + 1])))
            {
                // A required value is consumed greedily (commander semantics);
                // an optional one only when the next token is not an option.
                values.Add(args[++i]);
            }

            if (values.Count == 0)
            {
                if (spec.OptionalValue)
                {
                    result.Flags.Add(spec.Key);
                    continue;
                }

                result.Error ??= $"error: option '{spec.ErrorName}' argument missing";
                continue;
            }

            if (spec.Choices is { Length: > 0 })
            {
                foreach (var value in values)
                {
                    if (!spec.Choices.Contains(value, StringComparer.Ordinal) && spec.HiddenChoices?.Contains(value, StringComparer.Ordinal) != true)
                    {
                        result.Error ??=
                            $"error: option '{spec.ErrorName}' argument '{value}' is invalid. " +
                            $"Allowed choices are {string.Join(", ", spec.Choices)}.";
                    }
                }
            }

            if (spec.Validate is { } validate)
            {
                foreach (var value in values)
                {
                    if (validate(value) is { } reason)
                    {
                        result.Error ??=
                            $"error: option '{spec.ErrorName}' argument '{value}' is invalid. {reason}";
                    }
                }
            }

            if (!result.Values.TryGetValue(spec.Key, out var list))
            {
                result.Values[spec.Key] = list = [];
            }

            list.AddRange(values);
            continue;
        }

        return result;
    }

    /// <summary>
    /// A variadic list stops at the next option. A negative number ("-5") is a
    /// value, not an option; anything else starting with '-' ends the list.
    /// </summary>
    private static bool LooksLikeOption(string token) =>
        token.Length > 1 && token[0] == '-' && !char.IsAsciiDigit(token[1]);

    /// <summary>
    /// The reference's tool lists are "comma or space-separated"; space-separated
    /// entries arrive as separate tokens, comma-separated ones as one token.
    /// </summary>
    public static IReadOnlyList<string> SplitList(IReadOnlyList<string> raw) =>
        [.. raw.SelectMany(static v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))];
}
