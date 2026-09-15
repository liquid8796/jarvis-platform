namespace JarvisCode.App.Controls;

public enum SyntaxKind
{
    Default,
    Comment,
    String,
    Keyword,
    Number,
    Variable,
}

public readonly record struct SyntaxSpan(string Text, SyntaxKind Kind);

/// <summary>
/// Hand-rolled tokenizer behind the code displays. A character scanner rather than
/// regexes: one pass, no backtracking, and the output is lossless by construction —
/// the spans concatenate back to the input, so rendering can never drop a character.
/// Unknown languages come back as one Default span, which renders exactly as before.
/// </summary>
public static class SyntaxHighlighter
{
    /// <summary>Above this, highlighting is skipped: a wall of Runs would cost more than it shows.</summary>
    private const int MaxHighlightChars = 40_000;

    private sealed record LanguageSpec(
        string[] LineComments,
        (string Open, string Close)? BlockComment,
        string[] Keywords,
        bool TripleQuotes = false,
        bool VerbatimAt = false,
        bool Backticks = false,
        bool DollarVariables = false,
        bool CommandPosition = false,
        bool JsonKeys = false);

    private static readonly string[] CLikeKeywords =
    [
        "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event",
        "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "function", "goto", "if",
        "implicit", "import", "in", "int", "interface", "internal", "is", "let", "lock", "long", "namespace",
        "new", "null", "object", "operator", "out", "override", "params", "private", "protected", "public",
        "readonly", "record", "ref", "required", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc",
        "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong",
        "unchecked", "unsafe", "ushort", "using", "var", "virtual", "void", "volatile", "when", "where", "while",
        "with", "yield", "export", "extends", "implements", "instanceof", "of", "typeof", "undefined",
    ];

    private static readonly string[] PythonKeywords =
    [
        "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class", "continue", "def",
        "del", "elif", "else", "except", "finally", "for", "from", "global", "if", "import", "in", "is",
        "lambda", "nonlocal", "not", "or", "pass", "raise", "return", "try", "while", "with", "yield",
    ];

    /// <summary>In bash these keep the next word in command position instead of consuming it.</summary>
    private static readonly string[] BashKeywords =
    [
        "if", "then", "elif", "else", "fi", "for", "do", "done", "while", "until", "case", "esac", "function",
        "in", "select", "time", "export", "local", "readonly", "return", "exec", "eval", "sudo",
    ];

    private static readonly string[] PowerShellKeywords =
    [
        "begin", "break", "catch", "class", "continue", "do", "else", "elseif", "end", "enum", "filter",
        "finally", "for", "foreach", "function", "if", "in", "param", "process", "return", "switch", "throw",
        "try", "until", "using", "while",
    ];

    private static readonly LanguageSpec CLike = new(
        ["//"], ("/*", "*/"), CLikeKeywords, VerbatimAt: true, Backticks: true);

    private static readonly LanguageSpec Python = new(
        ["#"], null, PythonKeywords, TripleQuotes: true);

    private static readonly LanguageSpec Bash = new(
        ["#"], null, BashKeywords, DollarVariables: true, CommandPosition: true);

    private static readonly LanguageSpec PowerShell = new(
        ["#"], ("<#", "#>"), PowerShellKeywords, DollarVariables: true, CommandPosition: true);

    private static readonly LanguageSpec Json = new(
        ["//"], ("/*", "*/"), ["true", "false", "null"], JsonKeys: true);

    private static readonly Dictionary<string, LanguageSpec> Specs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs"] = CLike, ["csharp"] = CLike, ["c#"] = CLike,
        ["js"] = CLike, ["javascript"] = CLike, ["ts"] = CLike, ["typescript"] = CLike,
        ["jsx"] = CLike, ["tsx"] = CLike, ["java"] = CLike, ["c"] = CLike, ["cpp"] = CLike,
        ["go"] = CLike, ["rust"] = CLike, ["rs"] = CLike, ["kotlin"] = CLike, ["swift"] = CLike,
        ["py"] = Python, ["python"] = Python, ["ruby"] = Python, ["rb"] = Python,
        ["yaml"] = Python, ["yml"] = Python, ["toml"] = Python,
        ["sh"] = Bash, ["bash"] = Bash, ["shell"] = Bash, ["zsh"] = Bash, ["console"] = Bash,
        ["ps"] = PowerShell, ["ps1"] = PowerShell, ["powershell"] = PowerShell, ["pwsh"] = PowerShell,
        ["json"] = Json, ["jsonc"] = Json,
    };

    private static readonly HashSet<string> XmlLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "xml", "xaml", "html", "htm", "svg", "axaml",
    };

    public static IReadOnlyList<SyntaxSpan> Highlight(string? language, string code)
    {
        if (code.Length == 0)
            return [];
        if (code.Length > MaxHighlightChars || string.IsNullOrWhiteSpace(language))
            return [new SyntaxSpan(code, SyntaxKind.Default)];

        var name = language.Trim();
        if (XmlLanguages.Contains(name))
            return ScanXml(code);
        if (Specs.TryGetValue(name, out var spec))
            return Scan(code, spec);
        return [new SyntaxSpan(code, SyntaxKind.Default)];
    }

    // ---- generic scanner ----

    private static List<SyntaxSpan> Scan(string code, LanguageSpec spec)
    {
        var spans = new SpanBuilder(code);
        bool commandNext = spec.CommandPosition;
        int i = 0;
        while (i < code.Length)
        {
            char c = code[i];

            if (StartsWithAt(code, i, spec.LineComments) is { } linePrefix)
            {
                int end = code.IndexOf('\n', i);
                end = end < 0 ? code.Length : end;
                spans.Mark(i, end, SyntaxKind.Comment);
                i = end;
                continue;
            }

            if (spec.BlockComment is { } block && StartsAt(code, i, block.Open))
            {
                int close = code.IndexOf(block.Close, i + block.Open.Length, StringComparison.Ordinal);
                int end = close < 0 ? code.Length : close + block.Close.Length;
                spans.Mark(i, end, SyntaxKind.Comment);
                i = end;
                continue;
            }

            if (spec.TripleQuotes && (StartsAt(code, i, "\"\"\"") || StartsAt(code, i, "'''")))
            {
                var fence = code.Substring(i, 3);
                int close = code.IndexOf(fence, i + 3, StringComparison.Ordinal);
                int end = close < 0 ? code.Length : close + 3;
                spans.Mark(i, end, SyntaxKind.String);
                i = end;
                continue;
            }

            if (spec.VerbatimAt && c == '@' && i + 1 < code.Length && code[i + 1] == '"')
            {
                int end = ScanVerbatimString(code, i + 2);
                spans.Mark(i, end, SyntaxKind.String);
                i = end;
                continue;
            }

            if (c == '"' || c == '\'' || (spec.Backticks && c == '`'))
            {
                int end = ScanString(code, i, c);
                var kind = spec.JsonKeys && c == '"' && NextNonSpaceIs(code, end, ':')
                    ? SyntaxKind.Variable
                    : SyntaxKind.String;
                spans.Mark(i, end, kind);
                i = end;
                commandNext = false;
                continue;
            }

            if (spec.DollarVariables && c == '$')
            {
                int end = ScanDollar(code, i, out bool opensCommand);
                spans.Mark(i, end, SyntaxKind.Variable);
                i = end;
                if (opensCommand)
                    commandNext = true;
                continue;
            }

            if (char.IsDigit(c) && !IsWordChar(Prev(code, i)))
            {
                int end = i;
                while (end < code.Length && (char.IsLetterOrDigit(code[end]) || code[end] == '.' || code[end] == '_'))
                    end++;
                spans.Mark(i, end, SyntaxKind.Number);
                i = end;
                continue;
            }

            if (IsWordStart(c) || (spec.CommandPosition && commandNext && c == '.' ))
            {
                int end = i;
                while (end < code.Length && (IsWordChar(code[end]) ||
                       (spec.CommandPosition && (code[end] == '-' || code[end] == '.' || code[end] == '/'))))
                {
                    end++;
                }

                var word = code[i..end];
                if (spec.Keywords.Contains(word, StringComparer.Ordinal))
                {
                    spans.Mark(i, end, SyntaxKind.Keyword);
                    // In bash, "sudo git ..." still has its command ahead.
                }
                else if (spec.CommandPosition && commandNext)
                {
                    spans.Mark(i, end, SyntaxKind.Keyword);
                    commandNext = false;
                }

                i = end;
                continue;
            }

            if (spec.CommandPosition && c == '-' && commandNext)
            {
                // A line opening with a flag is not a command; stop waiting for one.
                commandNext = false;
            }

            if (spec.CommandPosition && (c == '\n' || c == '|' || c == ';' || c == '&' || c == '(' || c == '`'))
                commandNext = true;

            i++;
        }

        return spans.Finish();
    }

    private static int ScanString(string code, int start, char quote)
    {
        int i = start + 1;
        while (i < code.Length)
        {
            char c = code[i];
            if (c == '\\' && i + 1 < code.Length)
            {
                i += 2;
                continue;
            }

            if (c == quote)
                return i + 1;
            // A plain string does not survive a line break; bail out so one lost
            // quote cannot swallow the rest of the block.
            if (c == '\n' && quote != '`')
                return i;
            i++;
        }

        return code.Length;
    }

    private static int ScanVerbatimString(string code, int start)
    {
        int i = start;
        while (i < code.Length)
        {
            if (code[i] == '"')
            {
                if (i + 1 < code.Length && code[i + 1] == '"')
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return code.Length;
    }

    private static int ScanDollar(string code, int start, out bool opensCommand)
    {
        opensCommand = false;
        int i = start + 1;
        if (i < code.Length && code[i] == '{')
        {
            int close = code.IndexOf('}', i);
            return close < 0 ? code.Length : close + 1;
        }

        if (i < code.Length && code[i] == '(')
        {
            opensCommand = true;
            return i + 1;
        }

        while (i < code.Length && IsWordChar(code[i]))
            i++;
        return i;
    }

    // ---- xml / xaml ----

    private static List<SyntaxSpan> ScanXml(string code)
    {
        var spans = new SpanBuilder(code);
        int i = 0;
        while (i < code.Length)
        {
            if (StartsAt(code, i, "<!--"))
            {
                int close = code.IndexOf("-->", i + 4, StringComparison.Ordinal);
                int end = close < 0 ? code.Length : close + 3;
                spans.Mark(i, end, SyntaxKind.Comment);
                i = end;
                continue;
            }

            if (code[i] == '<')
            {
                int nameEnd = i + 1;
                if (nameEnd < code.Length && (code[nameEnd] == '/' || code[nameEnd] == '?' || code[nameEnd] == '!'))
                    nameEnd++;
                while (nameEnd < code.Length && (IsWordChar(code[nameEnd]) || code[nameEnd] is '.' or ':' or '-'))
                    nameEnd++;
                spans.Mark(i, nameEnd, SyntaxKind.Keyword);
                i = nameEnd;

                // Inside the tag: attribute names, quoted values, and the closer.
                while (i < code.Length && code[i] != '>')
                {
                    char c = code[i];
                    if (c == '"' || c == '\'')
                    {
                        int end = ScanString(code, i, c);
                        spans.Mark(i, end, SyntaxKind.String);
                        i = end;
                    }
                    else if (IsWordStart(c))
                    {
                        int end = i;
                        while (end < code.Length && (IsWordChar(code[end]) || code[end] is '.' or ':' or '-'))
                            end++;
                        spans.Mark(i, end, SyntaxKind.Variable);
                        i = end;
                    }
                    else
                    {
                        i++;
                    }
                }

                if (i < code.Length)
                {
                    spans.Mark(i, i + 1, SyntaxKind.Keyword);
                    i++;
                }

                continue;
            }

            i++;
        }

        return spans.Finish();
    }

    // ---- helpers ----

    private static bool IsWordStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static char Prev(string code, int i) => i > 0 ? code[i - 1] : '\0';

    private static bool StartsAt(string code, int i, string prefix) =>
        code.AsSpan(i).StartsWith(prefix, StringComparison.Ordinal);

    private static string? StartsWithAt(string code, int i, string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (StartsAt(code, i, prefix))
                return prefix;
        }

        return null;
    }

    private static bool NextNonSpaceIs(string code, int index, char expected)
    {
        while (index < code.Length && (code[index] == ' ' || code[index] == '\t'))
            index++;
        return index < code.Length && code[index] == expected;
    }

    /// <summary>
    /// Collects marked regions and emits the gaps between them as Default, merging
    /// neighbours of one kind — losslessness lives here, not in the scanners.
    /// </summary>
    private sealed class SpanBuilder(string code)
    {
        private readonly List<SyntaxSpan> _spans = [];
        private int _emitted;

        public void Mark(int start, int end, SyntaxKind kind)
        {
            if (start > _emitted)
                Add(code[_emitted..start], SyntaxKind.Default);
            if (end > start)
                Add(code[start..end], kind);
            _emitted = Math.Max(_emitted, end);
        }

        public List<SyntaxSpan> Finish()
        {
            if (_emitted < code.Length)
                Add(code[_emitted..], SyntaxKind.Default);
            return _spans;
        }

        private void Add(string text, SyntaxKind kind)
        {
            if (text.Length == 0)
                return;
            if (_spans.Count > 0 && _spans[^1].Kind == kind)
            {
                _spans[^1] = new SyntaxSpan(_spans[^1].Text + text, kind);
                return;
            }

            _spans.Add(new SyntaxSpan(text, kind));
        }
    }
}
