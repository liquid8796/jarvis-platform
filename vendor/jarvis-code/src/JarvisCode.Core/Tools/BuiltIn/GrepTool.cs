using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace JarvisCode.Core.Tools.BuiltIn;

/// <summary>
/// Content search over the workspace, taking the reference Grep tool's ripgrep-shaped
/// arguments: <c>glob</c>, <c>type</c>, <c>-i</c>, <c>-n</c>, <c>-o</c>, <c>-A</c>/<c>-B</c>/<c>-C</c>
/// (or <c>context</c>), <c>head_limit</c>, <c>offset</c>, <c>multiline</c> and <c>output_mode</c>.
/// The names this port used before (<c>Glob</c>, <c>case_insensitive</c>, <c>context_lines</c>,
/// <c>no_ignore</c>) are still read, so a stored session replays.
/// </summary>
public sealed class GrepTool : ITool
{
    private const int MaxMatches = 200;

    /// <summary>Per-file share of the budget, so one busy file cannot swallow the whole result.</summary>
    private const int MaxMatchesPerFile = 30;

    private const int MaxLineChars = 400;
    private const int MaxContextLines = 10;

    /// <summary>The reference's default page: "Defaults to 250 when unspecified. Pass 0 for unlimited".</summary>
    public const int DefaultHeadLimit = 250;

    private const string ContentMode = "content";
    private const string FilesMode = "files_with_matches";
    private const string CountMode = "count";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// ripgrep's file types, as far as this tool knows them: the reference passes
    /// <c>type</c> straight to <c>rg --type</c>, and these are the extension sets
    /// ripgrep's own <c>--type-list</c> prints for the names a model is likely to send.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> FileTypes =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["js"] = [".js", ".jsx", ".mjs", ".cjs", ".vue"],
            ["ts"] = [".ts", ".tsx", ".cts", ".mts"],
            ["py"] = [".py", ".pyi"],
            ["python"] = [".py", ".pyi"],
            ["rust"] = [".rs"],
            ["go"] = [".go"],
            ["java"] = [".java", ".jsp", ".jspx", ".properties"],
            ["cs"] = [".cs"],
            ["csharp"] = [".cs"],
            ["c"] = [".c", ".h", ".H", ".cats"],
            ["cpp"] = [".C", ".H", ".cc", ".cpp", ".cxx", ".c++", ".hh", ".hpp", ".hxx", ".h++", ".inl"],
            ["h"] = [".h", ".hh", ".hpp"],
            ["json"] = [".json", ".sarif"],
            ["md"] = [".markdown", ".md", ".mdown", ".mkdn"],
            ["markdown"] = [".markdown", ".md", ".mdown", ".mkdn"],
            ["yaml"] = [".yaml", ".yml"],
            ["toml"] = [".toml"],
            ["xml"] = [".xml", ".xml.dist", ".dtd", ".xsl", ".xslt", ".xsd", ".xjb", ".rng", ".sch", ".xhtml"],
            ["html"] = [".htm", ".html", ".ejs"],
            ["css"] = [".css", ".scss"],
            ["sass"] = [".sass", ".scss"],
            ["less"] = [".less"],
            ["sh"] = [".bash", ".bashrc", ".sh", ".zsh", ".fish", ".ksh", ".csh", ".tcsh"],
            ["ps1"] = [".ps1", ".psm1", ".psd1"],
            ["rb"] = [".rb", ".rbw", ".gemspec", ".rake"],
            ["ruby"] = [".rb", ".rbw", ".gemspec", ".rake"],
            ["php"] = [".php", ".php3", ".php4", ".php5", ".phtml"],
            ["swift"] = [".swift"],
            ["kotlin"] = [".kt", ".kts"],
            ["scala"] = [".scala", ".sbt"],
            ["sql"] = [".sql", ".psql"],
            ["lua"] = [".lua"],
            ["dart"] = [".dart"],
            ["elixir"] = [".ex", ".eex", ".exs", ".heex", ".leex", ".livemd"],
            ["erlang"] = [".erl", ".hrl"],
            ["haskell"] = [".hs", ".lhs"],
            ["ocaml"] = [".ml", ".mli", ".mll", ".mly"],
            ["zig"] = [".zig"],
            ["r"] = [".R", ".r", ".Rmd", ".Rnw"],
            ["tex"] = [".tex", ".ltx", ".cls", ".sty", ".bib", ".dtx", ".ins"],
            ["txt"] = [".txt"],
            ["csv"] = [".csv"],
            ["cmake"] = [".cmake"],
            ["make"] = [".mk", ".mak"],
            ["docker"] = [".dockerfile"],
            ["proto"] = [".proto"],
            ["svg"] = [".svg"],
            ["xaml"] = [".xaml"],
        };

    /// <summary>One resolved request, passed down to the per-file scanners.</summary>
    private sealed record Search(
        Regex Pattern,
        string Mode,
        int Before,
        int After,
        bool LineNumbers,
        bool OnlyMatching,
        bool Multiline,
        string WorkingDirectory);

    public string Name => "Grep";

    public string Description =>
        "Searches file contents recursively with a regular expression and returns the matching file paths, " +
        "relative to the working directory. output_mode switches to 'path:line: text' matches or per-file counts. " +
        "Binary files, build/VCS directories and git-ignored files are skipped.";

    public JsonObject InputSchema => SchemaBuilder.Object(
        [
            ("pattern", SchemaBuilder.String("The regular expression pattern to search for in file contents")),
            ("path", SchemaBuilder.String("File or directory to search in (rg PATH). Defaults to current working directory.")),
            ("glob", SchemaBuilder.String(
                "Glob pattern to filter files (e.g. \"*.js\", \"*.{ts,tsx}\") - maps to rg --glob")),
            ("output_mode", SchemaBuilder.String(
                "Output mode: \"content\" shows matching lines (supports -A/-B/-C context, -n line numbers, " +
                "head_limit), \"files_with_matches\" shows file paths (supports head_limit), \"count\" shows " +
                "match counts (supports head_limit). Defaults to \"files_with_matches\".")),
            ("-B", SchemaBuilder.Integer(
                "Number of lines to show before each match (rg -B). Requires output_mode: \"content\", ignored otherwise.")),
            ("-A", SchemaBuilder.Integer(
                "Number of lines to show after each match (rg -A). Requires output_mode: \"content\", ignored otherwise.")),
            ("-C", SchemaBuilder.Integer(
                "Number of lines to show before and after each match (rg -C). Requires output_mode: \"content\", ignored otherwise.")),
            ("context", SchemaBuilder.Integer("Alias for -C.")),
            ("-n", SchemaBuilder.Boolean(
                "Show line numbers in output (rg -n). Requires output_mode: \"content\", ignored otherwise. Defaults to true.")),
            ("-i", SchemaBuilder.Boolean("Case insensitive search (rg -i)")),
            ("-o", SchemaBuilder.Boolean(
                "Print only the matched (non-empty) parts of a matching line, with each such part on a separate " +
                "output line (rg -o / --only-matching). Requires output_mode: \"content\", ignored otherwise. Defaults to false.")),
            ("type", SchemaBuilder.String(
                "File type to search (rg --type). Common types: js, py, rust, go, java, etc. More efficient than " +
                "include for standard file types.")),
            ("head_limit", SchemaBuilder.Integer(
                "Limit output to first N lines/entries, equivalent to \"| head -N\". Works across all output modes: " +
                "content (limits output lines), files_with_matches (limits file paths), count (limits count entries). " +
                "Defaults to 250 when unspecified. Pass 0 for unlimited (use sparingly — total output is still " +
                "capped by the tool's own budget).")),
            ("offset", SchemaBuilder.Integer(
                "Skip first N lines/entries before applying head_limit, equivalent to \"| tail -n +N | head -N\". " +
                "Works across all output modes. Defaults to 0.")),
            ("multiline", SchemaBuilder.Boolean(
                "Enable multiline mode where . matches newlines and patterns can span lines (rg -U --multiline-dotall). Default: false.")),
        ],
        "pattern");

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments) =>
        $"Grep({JsonArgs.GetString(arguments, "pattern") ?? "?"})";

    public async Task<ToolResult> ExecuteAsync(
        JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var patternText = JsonArgs.GetString(arguments, "pattern");
        if (string.IsNullOrEmpty(patternText))
            return ToolResult.Error("pattern is required.");

        // The schema this tool advertises is the reference's, and it says
        // files_with_matches. An absent mode has to read as what the model was told.
        var mode = (JsonArgs.GetString(arguments, "output_mode") ?? FilesMode).Trim().ToLowerInvariant();
        if (mode is not (ContentMode or FilesMode or CountMode))
            return ToolResult.Error($"Unknown output_mode '{mode}'. Use {ContentMode}, {FilesMode} or {CountMode}.");

        bool multiline = JsonArgs.GetBool(arguments, "multiline");
        bool ignoreCase = JsonArgs.GetBool(arguments, "-i") || JsonArgs.GetBool(arguments, "case_insensitive");
        Regex pattern;
        try
        {
            var options = RegexOptions.Compiled;
            if (ignoreCase)
                options |= RegexOptions.IgnoreCase;
            if (multiline)
                options |= RegexOptions.Singleline;
            pattern = new Regex(patternText, options, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Error($"Invalid regular expression: {ex.Message}");
        }

        string[]? typeExtensions = null;
        if (JsonArgs.GetString(arguments, "type") is { Length: > 0 } type)
        {
            if (!FileTypes.TryGetValue(type.Trim(), out typeExtensions))
            {
                return ToolResult.Error(
                    $"Unknown file type '{type}'. Known types: {string.Join(", ", FileTypes.Keys.OrderBy(static k => k, StringComparer.Ordinal))}. " +
                    "Use glob for anything else.");
            }
        }

        var root = context.ResolvePath(JsonArgs.GetString(arguments, "path") ?? ".");
        IReadOnlyList<string> files;
        if (File.Exists(root))
        {
            files = [root];
        }
        else if (Directory.Exists(root))
        {
            files = await FileSystemDefaults.CollectSearchableFilesAsync(
                root,
                NormalizeGlob(JsonArgs.GetString(arguments, "glob") ?? JsonArgs.GetString(arguments, "Glob")),
                JsonArgs.GetBool(arguments, "no_ignore"),
                cancellationToken);
        }
        else
        {
            return ToolResult.Error($"Path not found: {root}");
        }

        if (typeExtensions is not null)
        {
            files = [.. files.Where(f => typeExtensions.Any(
                ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))];
        }

        // -C (or its alias context, or this tool's older context_lines) sets both
        // sides; -A and -B each override their own side.
        int both = Math.Clamp(
            JsonArgs.GetInt(arguments, "-C") ?? JsonArgs.GetInt(arguments, "context") ??
            JsonArgs.GetInt(arguments, "context_lines") ?? 0,
            0, MaxContextLines);
        int before = Math.Clamp(JsonArgs.GetInt(arguments, "-B") ?? both, 0, MaxContextLines);
        int after = Math.Clamp(JsonArgs.GetInt(arguments, "-A") ?? both, 0, MaxContextLines);
        bool lineNumbers = arguments["-n"] is null || JsonArgs.GetBool(arguments, "-n");
        bool onlyMatching = JsonArgs.GetBool(arguments, "-o");

        var search = new Search(pattern, mode, before, after, lineNumbers, onlyMatching, multiline, context.WorkingDirectory);
        var result = await RunAsync(search, files, patternText, root, context, cancellationToken);
        if (result.IsError)
            return result;

        int headLimit = Math.Max(0, JsonArgs.GetInt(arguments, "head_limit") ?? DefaultHeadLimit);
        int offset = Math.Max(0, JsonArgs.GetInt(arguments, "offset") ?? 0);
        return ToolResult.Success(context.Truncate(Page(result.Content, offset, headLimit), "match list"));
    }

    /// <summary>
    /// The reference's <c>head_limit</c>/<c>offset</c> page over the output lines:
    /// "| tail -n +N | head -N". A page that leaves lines behind says how many.
    /// </summary>
    internal static string Page(string output, int offset, int headLimit)
    {
        if (offset == 0 && headLimit == 0)
            return output;

        var lines = output.Split('\n');
        var remaining = Math.Max(0, lines.Length - offset);
        var take = headLimit == 0 ? remaining : Math.Min(headLimit, remaining);
        if (offset == 0 && take >= lines.Length)
            return output;

        var page = lines.Skip(offset).Take(take).ToList();
        var left = lines.Length - offset - take;
        if (left > 0)
        {
            page.Add($"... ({left} more line(s); continue with offset={offset + take})");
        }
        else if (offset >= lines.Length)
        {
            page.Add($"(offset {offset} is past the end of the {lines.Length}-line output)");
        }

        return string.Join('\n', page);
    }

    private static async Task<ToolResult> RunAsync(
        Search search, IReadOnlyList<string> files, string patternText, string root,
        ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        int totalMatches = 0;
        int filesSearched = 0;
        int filesWithMatches = 0;
        int filesTooLarge = 0;
        int index = 0;

        // Counting stops at a number of files rather than of matches: a per-file cap
        // would report a wrong count for the very files worth counting.
        bool countingFiles = search.Mode is CountMode or FilesMode;
        for (; index < files.Count && (countingFiles ? filesWithMatches : totalMatches) < MaxMatches; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            if (FileSystemDefaults.LooksBinary(file))
                continue;

            // Only the multiline scanner needs the file whole; line scanning streams.
            if (search.Multiline && FileSystemDefaults.CheckReadableSize(file) is not null)
            {
                filesTooLarge++;
                continue;
            }

            filesSearched++;
            var display = DisplayPath(file, search.WorkingDirectory);
            int budget = countingFiles ? int.MaxValue : Math.Min(MaxMatchesPerFile, MaxMatches - totalMatches);
            int matches;
            try
            {
                matches = search.Multiline
                    ? await ScanWholeFileAsync(builder, file, display, search, budget, cancellationToken)
                    : await ScanLinesAsync(builder, file, display, search, budget, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (matches == 0)
                continue;

            totalMatches += matches;
            filesWithMatches++;
            if (search.Mode == FilesMode)
                builder.AppendLine(display);
            else if (search.Mode == CountMode)
                builder.AppendLine($"{display}: {matches}");
        }

        if (filesWithMatches == 0)
        {
            var scope = filesSearched == 1 ? "1 file" : $"{filesSearched} files";
            return ToolResult.Success($"No matches for /{patternText}/ in {scope} under {root}.");
        }

        if ((countingFiles ? filesWithMatches : totalMatches) >= MaxMatches && index < files.Count)
        {
            var unit = countingFiles ? "matching files" : "matches";
            builder.AppendLine(
                $"... (stopped at {MaxMatches} {unit}; {files.Count - index} of {files.Count} files not searched)");
        }

        if (filesTooLarge > 0)
            builder.AppendLine($"... ({filesTooLarge} file(s) skipped: multiline search needs the file in memory)");
        if (search.Mode == CountMode)
            builder.AppendLine($"Total: {totalMatches} match(es) in {filesWithMatches} file(s).");

        return ToolResult.Success(builder.ToString().TrimEnd());
    }

    /// <summary>
    /// Streams the file a line at a time, keeping only the context window in memory, and
    /// stops as soon as this file's share of the match budget is used up.
    /// </summary>
    private static async Task<int> ScanLinesAsync(
        StringBuilder builder, string file, string display, Search search, int budget,
        CancellationToken cancellationToken)
    {
        int matches = 0;
        int lineNumber = 0;
        int lastPrinted = 0;
        int trailing = 0;
        var leading = new Queue<(int Number, string Text)>();

        using var reader = new StreamReader(file, detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lineNumber++;
            bool isMatch;
            try
            {
                isMatch = search.Pattern.IsMatch(line);
            }
            catch (RegexMatchTimeoutException)
            {
                isMatch = false;
            }

            if (isMatch)
            {
                matches++;
                if (search.Mode == ContentMode)
                {
                    while (leading.Count > 0)
                    {
                        var (number, text) = leading.Dequeue();
                        if (number <= lastPrinted)
                            continue;
                        AppendGap(builder, lastPrinted, number);
                        builder.AppendLine(Format(display, number, text, matched: false, search));
                        lastPrinted = number;
                    }

                    AppendGap(builder, lastPrinted, lineNumber);
                    if (search.OnlyMatching)
                    {
                        // rg -o: each non-empty match on its own line.
                        foreach (var part in MatchedParts(search.Pattern, line))
                        {
                            builder.AppendLine(Format(display, lineNumber, part, matched: true, search));
                        }
                    }
                    else
                    {
                        builder.AppendLine(Format(display, lineNumber, line, matched: true, search));
                    }

                    lastPrinted = lineNumber;
                    trailing = search.After;
                }

                if (search.Mode == FilesMode || matches >= budget)
                    break;
            }
            else if (trailing > 0 && search.Mode == ContentMode)
            {
                AppendGap(builder, lastPrinted, lineNumber);
                builder.AppendLine(Format(display, lineNumber, line, matched: false, search));
                lastPrinted = lineNumber;
                trailing--;
            }

            if (search.Before > 0)
            {
                leading.Enqueue((lineNumber, line));
                if (leading.Count > search.Before)
                    leading.Dequeue();
            }
        }

        return matches;
    }

    private static IEnumerable<string> MatchedParts(Regex pattern, string line)
    {
        Match match;
        try
        {
            match = pattern.Match(line);
        }
        catch (RegexMatchTimeoutException)
        {
            yield break;
        }

        while (match.Success)
        {
            if (match.Length > 0)
                yield return match.Value;
            try
            {
                match = match.NextMatch();
            }
            catch (RegexMatchTimeoutException)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// Multiline mode: the pattern is matched against the file as one string, so a match
    /// can span line breaks. Each hit is reported at the line it starts on.
    /// </summary>
    private static async Task<int> ScanWholeFileAsync(
        StringBuilder builder, string file, string display, Search search, int budget,
        CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(file, cancellationToken);
        int matches = 0;
        int position = 0;
        while (position <= text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Match match;
            try
            {
                match = search.Pattern.Match(text, position);
            }
            catch (RegexMatchTimeoutException)
            {
                break;
            }

            if (!match.Success)
                break;

            matches++;
            if (search.Mode == ContentMode)
            {
                builder.AppendLine(Format(
                    display, LineNumberAt(text, match.Index), match.Value.ReplaceLineEndings("\\n"), matched: true, search));
            }

            if (search.Mode == FilesMode || matches >= budget)
                break;
            // An empty match would otherwise pin the position and loop forever.
            position = match.Index + Math.Max(1, match.Length);
        }

        return matches;
    }

    private static int LineNumberAt(string text, int index)
    {
        int line = 1;
        for (int i = 0; i < index; i++)
        {
            if (text[i] == '\n')
                line++;
        }

        return line;
    }

    /// <summary>ripgrep's divider, printed when the block being started is not adjacent.</summary>
    private static void AppendGap(StringBuilder builder, int lastPrinted, int next)
    {
        if (lastPrinted > 0 && next > lastPrinted + 1)
            builder.AppendLine("--");
    }

    private static string Format(string display, int lineNumber, string text, bool matched, Search search)
    {
        var trimmed = text.Trim();
        if (trimmed.Length > MaxLineChars)
            trimmed = trimmed[..MaxLineChars] + "…";
        if (!search.LineNumbers)
            return matched ? $"{display}: {trimmed}" : $"{display}- {trimmed}";
        return matched ? $"{display}:{lineNumber}: {trimmed}" : $"{display}-{lineNumber}- {trimmed}";
    }

    /// <summary>
    /// Paths print relative to the working directory rather than the search root, so the
    /// same file reads the same whichever directory the call happened to search; a file
    /// outside the workspace keeps its absolute path.
    /// </summary>
    private static string DisplayPath(string file, string workingDirectory)
    {
        if (string.IsNullOrEmpty(workingDirectory))
            return file;
        try
        {
            var relative = Path.GetRelativePath(workingDirectory, file);
            return relative.StartsWith("..", StringComparison.Ordinal) ? file : relative;
        }
        catch (ArgumentException)
        {
            return file;
        }
    }

    /// <summary>
    /// A bare name filter keeps its historical meaning — that file name in any directory —
    /// while anything carrying a separator is treated as a path glob.
    /// </summary>
    private static string? NormalizeGlob(string? glob)
    {
        if (string.IsNullOrWhiteSpace(glob))
            return null;
        // The globbing library speaks in forward slashes whatever the platform writes.
        var trimmed = glob.Trim().Replace(Path.DirectorySeparatorChar, '/');
        return trimmed.Contains('/') ? trimmed : "**/" + trimmed;
    }
}
