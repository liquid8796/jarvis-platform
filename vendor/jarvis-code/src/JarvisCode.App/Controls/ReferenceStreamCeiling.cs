using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace JarvisCode.App.Controls;

/// <summary>
/// The installed desktop's streaming ceiling (shared-13-DPMkEUSi.js,
/// ys/zs/Bs/Xs/qs). Chat protects unfinished constructs with a 600-character
/// maximum lookback; Code's current alluvium caller holds block markers only.
/// </summary>
internal static class ReferenceStreamCeiling
{
    private const string QuotePrefix = @"[ \t]*(?:>[ \t]*)*";
    private const string AtxPrefix = @" {0,3}(?:>[ \t]*)*";
    private static readonly Regex ListMarker = new("^" + QuotePrefix + @"(?:(?:[-*+]|\d{1,9}[.)]?)$|(?:[-*+]|\d{1,9}[.)])[ \t]+(?:\[(?:[ xX](?:\][ \t]*)?)?)?$)");
    private static readonly Regex HeadingMarker = new("^" + AtxPrefix + @"#{1,6}[ \t]*$");
    private static readonly Regex QuoteMarker = new(@"^[ \t]*(?:>[ \t]*)+$");
    private static readonly Regex FenceMarker = new(@"^[ \t>]*(?:`+|~+)[ \t]*$");
    private static readonly Regex FenceStart = new(@"^[ \t>]*(?:(`{3,})(?=[^`]*$)|(~{3,}))");
    private static readonly Regex Cell = new(@"^(\$\w{1,255}!?)?\$?[A-Z]{1,3}\$?[0-9]{1,7}(:(\$\w{1,255}!?)?\$?[A-Z]{1,3}\$?[0-9]{1,7})?", RegexOptions.ECMAScript);
    private static readonly Regex Variable = new(@"\$(?:[?!@#*]|[A-Z][A-Z0-9]+(?:_[A-Z][A-Z0-9]*)*(?=[/.:)]|,? [\p{Ll}\p{Lo}\p{N}]))");
    private static readonly Regex ProseInMath = new(@"(?<![\\\p{L}])\p{L}{4}|[\u3400-\u9fff\u3040-\u30ff\uac00-\ud7af]{2}|(?<!\\)%");
    private static readonly Regex NumberProse = new(@"^\d[\d.,]*(?:[a-z]{1,2}|/[a-z]+)?[*_~)\]:;]*\s+\p{L}", RegexOptions.IgnoreCase);
    private static readonly Regex TrailingOperator = new(@"(?:(?<![_^])[-+*]|[=<>/^_({[,:\\\u00d7\u2212\u00b7\u00f7])\s*$");
    private static readonly Regex MathOperator = new(@"[\\{}^_=<>]");
    private static readonly Regex EarlyMathOperator = new(@"[\\^_{]");
    private const string Operand = @"-?(?:\d+(?:[.,]\d+)*[a-z\u03b1-\u03c9]{0,2}|[a-z\u03b1-\u03c9])[!\u00b0]*";
    private const string Separator = @"(?: ?[-+*/:\u00d7\u2212\u00b7\u00f7\u2264\u2265\u2260\u2248] ?|, )";
    private static readonly string Term = "(?:" + Operand + @"|\(" + Operand + "(?:" + Separator + Operand + @")*\))";
    private static readonly Regex SimpleMath = new("^" + Term + "(?:(?:" + Separator + @"|(?=\())" + Term + ")*$", RegexOptions.IgnoreCase);

    public static int Ceiling(string text, bool holdBack)
    {
        if (text.Length == 0) return 0;
        if (!holdBack)
        {
            var start = text.LastIndexOf('\n', text.Length - 1) + 1;
            var tail = text[start..];
            return SurrogateSafe(text, ListMarker.IsMatch(tail) || HeadingMarker.IsMatch(tail) ? start : text.Length);
        }
        return SurrogateSafe(text, Math.Max(ProtectedCeiling(text), text.Length - 600));
    }

    private static int ProtectedCeiling(string text)
    {
        var end = text.Length;
        var row = text.LastIndexOf('\n', end - 1) + 1;
        if (InFence(text, row)) return FenceMarker.IsMatch(text[row..]) ? row : WordBoundary(text, row, end);
        if (IsBlockMath(text, row)) return row;
        var tail = text[row..];
        if (ListMarker.IsMatch(tail) || QuoteMarker.IsMatch(tail)) return row;
        var code = -1; var codeRun = 0; var bracket = -1; var math = -1; var protectedTo = -1;
        var firstUnclosedMath = -1;
        var emphasis = new Dictionary<string, int>();
        var committed = row;
        for (var position = row; position < end;)
        {
            var character = text[position];
            if (character == '`' && position >= protectedTo)
            {
                var length = RunLength(text, position);
                if (code < 0) { code = position; codeRun = length; }
                else if (length == codeRun) { code = -1; committed = position + length; }
                position += length;
                continue;
            }
            if (code >= 0) { position++; continue; }
            if (bracket < 0 && position >= protectedTo && character is '$' or '\\')
            {
                var next = character == '$' ? Dollar(text, position, row, ref firstUnclosedMath)
                    : Backslash(text, position, ref firstUnclosedMath);
                if (next == -1 || next > end) { math = position; break; }
                if (next == -2) { position++; continue; }
                if (next > position + 1 && At(text, next - 1) == (character == '$' ? '$' : ')'))
                { committed = next; protectedTo = next; position++; continue; }
                position = next;
                continue;
            }
            if (character == ':' && bracket < 0 && position >= protectedTo && text.AsSpan(position + 1).StartsWith("ant"))
            {
                var nameEnd = position + 4;
                while (AsciiWord(At(text, nameEnd)) || At(text, nameEnd) == '-') nameEnd++;
                if (At(text, nameEnd) == '[')
                {
                    var close = text.IndexOf(']', nameEnd + 1);
                    var next = close + 1; var attributes = false;
                    if (close >= 0 && At(text, next) == '{') { var last = text.IndexOf('}', next + 1); next = last + 1; attributes = last >= 0; }
                    if (next == 0 || next > end || !attributes && next == end) { bracket = position; break; }
                    committed = next; position = next; continue;
                }
            }
            if (position >= protectedTo && (character == '[' || character == '!' && At(text, position + 1) == '['))
            { if (bracket < 0) bracket = position; position += character == '!' ? 2 : 1; continue; }
            if (character == ']' && bracket >= 0)
            {
                if (position + 1 == end) break;
                if (At(text, position + 1) == '(')
                {
                    var closing = text.IndexOf(')', position + 2);
                    if (closing < 0) break;
                    bracket = -1; position = closing + 1; committed = position; continue;
                }
                bracket = -1; committed = ++position; continue;
            }
            if (character is '*' or '_' or '~')
            {
                var length = RunLength(text, position);
                var before = position > row ? text[position - 1] : '\0';
                var after = At(text, position + length);
                var canOpen = position >= protectedTo && (after == '\0' || !Whitespace(after)) && !(character == '_' && char.IsLetterOrDigit(before));
                var canClose = before != '\0' && !Whitespace(before);
                var insideWord = character == '_' && AsciiWord(before) && AsciiWord(after);
                void Toggle(string key)
                {
                    if (emphasis.ContainsKey(key)) { if (canClose) { emphasis.Remove(key); committed = Math.Max(committed, position + length); } }
                    else if (canOpen) emphasis[key] = position;
                }
                if (!insideWord)
                {
                    if (length >= 2) Toggle(new string(character, 2));
                    if (character != '~' && length % 2 == 1) Toggle(character.ToString());
                }
                position += length; continue;
            }
            position++;
        }
        var ceiling = end;
        if (code >= 0) ceiling = Math.Min(ceiling, code);
        if (bracket >= 0) ceiling = Math.Min(ceiling, bracket);
        if (math >= 0) ceiling = Math.Min(ceiling, WordBoundary(text, committed, math));
        foreach (var opening in emphasis.Values) ceiling = Math.Min(ceiling, opening);
        if (ceiling == end) ceiling = WordBoundary(text, committed, end, row);
        if (ceiling < end && ceiling > row && QuoteMarker.IsMatch(text[row..ceiling])) ceiling = row;
        if (ceiling > row && (HeadingMarker.IsMatch(text[row..ceiling]) || ListMarker.IsMatch(text[row..ceiling]) ||
            ceiling < end && HeadingMarker.IsMatch(tail))) return row;
        return ceiling;
    }

    private static int Dollar(string text, int start, int row, ref int firstUnclosed)
    {
        if (At(text, start - 1) == '$' || Escaped(text, start)) return start + 1;
        if (start + 1 == text.Length) return -1;
        var following = text[start + 1];
        if (following is '$' or ' ') return start + 1;
        var number = char.IsAsciiDigit(following);
        if (number && AsciiAlphaNumeric(At(text, start - 1))) return start + 1;
        if (!number)
        {
            var cellStart = start;
            var limit = Math.Max(row, start - 560);
            while (cellStart > limit && text[cellStart - 1] != ' ') cellStart--;
            if (!(cellStart == limit && limit > row && text[cellStart - 1] != ' '))
            {
                var cell = Cell.Match(text[cellStart..Math.Min(text.Length, cellStart + 560)]);
                if (cell.Success)
                {
                    var last = cellStart + cell.Length - 1;
                    if (text[cellStart] == '$' && last + 1 == text.Length) return -1;
                    if (!(text[cellStart] == '$' && At(text, last + 1) == '$') && last > start) return last + 1;
                }
            }
        }
        var codeLimit = CodeLimit(text, start + 1);
        var end = Math.Min(codeLimit, start + 81);
        var closing = IndexBefore(text, '$', start + 1, end);
        while (closing >= 0 && Escaped(text, closing)) closing = IndexBefore(text, '$', closing + 1, end);
        if (closing < 0)
        {
            var candidate = text[(start + 1)..end];
            if (number && (!PlausibleMath(candidate) || LongPlainMath(candidate))) return start + 1;
            return UnclosedMath(text, start, codeLimit, ref firstUnclosed);
        }
        if (text[closing - 1] == ' ') return start + 1;
        if (number)
        {
            var candidate = text[(start + 1)..closing];
            if (!NumericMath(candidate)) return start + 1;
            if (closing + 1 == text.Length) return LongPlainMath(candidate) ? start + 1 : UnclosedMath(text, start, codeLimit, ref firstUnclosed);
            var next = text[closing + 1];
            return LatinOrGreek(next) || char.IsDigit(next) || next is '$' or '\\' ? start + 1 : closing + 1;
        }
        if (closing + 1 == text.Length) return UnclosedMath(text, start, codeLimit, ref firstUnclosed);
        return char.IsAsciiDigit(text[closing + 1]) ? start + 1 : closing + 1;
    }

    private static int Backslash(string text, int start, ref int firstUnclosed)
    {
        var last = start;
        while (At(text, last + 1) == '\\') last++;
        if ((last - start) % 2 == 1) return last + 1;
        if (last + 1 == text.Length) return -1;
        if (text[last + 1] != '(') return AsciiPunctuation(text[last + 1]) ? last + 2 : last + 1;
        var opening = last + 1;
        var codeLimit = CodeLimit(text, opening + 1);
        var end = Math.Min(codeLimit, start + 81);
        for (var closing = IndexBefore(text, ')', opening + 1, end); closing >= 0; closing = IndexBefore(text, ')', closing + 1, end))
        {
            var slashes = 0;
            while (closing - 1 - slashes > opening && text[closing - 1 - slashes] == '\\') slashes++;
            if (slashes % 2 != 0) return closing == opening + 2 ? opening + 1 : closing + 1;
        }
        return UnclosedMath(text, start, codeLimit, ref firstUnclosed);
    }

    private static int UnclosedMath(string text, int start, int end, ref int first)
    {
        var variable = Variable.Match(text, start);
        if (end != text.Length || variable.Success && variable.Index == start) return -2;
        if (first < 0) first = start;
        return text.Length - first <= 80 ? -1 : -2;
    }

    private static bool IsBlockMath(string text, int row)
    {
        var start = row;
        while (At(text, start) is ' ' or '\t' or '>') start++;
        var dollars = At(text, start) == '$' && At(text, start + 1) == '$';
        if (!dollars && (At(text, start) != '\\' || At(text, start + 1) is not '[' and not ']')) return false;
        var tail = text[start..].Trim();
        if (tail.StartsWith("$$$", StringComparison.Ordinal)) return false;
        if (Regex.IsMatch(tail, dollars ? @"^\$\$.+\$\$\s*[^\s$]" : @"^\\\[.*\\\]\s*\S")) return false;
        return !(tail.Length > 162 && !EarlyMathOperator.IsMatch(tail[2..162]));
    }

    private static bool InFence(string text, int row)
    {
        var ranges = new List<int>();
        var fence = '\0'; var length = 0; var quoteDepth = 0;
        for (var start = 0; start < text.Length;)
        {
            var end = text.IndexOf('\n', start); if (end < 0) end = text.Length;
            var position = start; var depth = 0;
            while (position < end && At(text, position) is ' ' or '\t' or '>') { if (text[position] == '>') depth++; position++; }
            if (fence != '\0' && depth < quoteDepth) { ranges.Add(start); fence = '\0'; }
            if (At(text, position) is '`' or '~')
            {
                if (fence == '\0')
                {
                    var match = FenceStart.Match(text[start..end]);
                    if (match.Success) { var run = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value; fence = run[0]; length = run.Length; quoteDepth = depth; ranges.Add(end + 1); }
                }
                else if (text[position] == fence && depth == quoteDepth)
                {
                    var runEnd = position; while (At(text, runEnd) == fence) runEnd++;
                    var trailing = runEnd; while (trailing < end && At(text, trailing) is ' ' or '\t') trailing++;
                    if (runEnd - position >= length && trailing == end) { ranges.Add(start); fence = '\0'; }
                }
            }
            start = end + 1;
        }
        if (fence != '\0') ranges.Add(int.MaxValue);
        for (var i = ranges.Count - 2; i >= 0; i -= 2) if (ranges[i] <= row) return row < ranges[i + 1];
        return false;
    }

    private static int WordBoundary(string text, int from, int end, int? row = null)
    {
        if (end > from && At(text, end - 1) is not ' ' and not '\t')
        {
            for (var i = end - 2; i >= Math.Max(from, end - 25); i--) if (text[i] is ' ' or '\t') return i + 1;
            if (row == from && end - from <= 24 && WordOpening(At(text, from)) && AllowedWord(text[from..end]) && FirstNonspace(text) < from) return from;
        }
        return end;
    }

    private static string OutsideBraces(string text)
    {
        var depth = 0; var result = new StringBuilder();
        foreach (var character in text)
        {
            if (character == '{') depth++;
            else if (character == '}' && depth > 0) depth--;
            else if (depth == 0) { result.Append(character); continue; }
            result.Append(' ');
        }
        return result.ToString();
    }
    private static bool NumericMath(string text) => !NumberProse.IsMatch(text) && !TrailingOperator.IsMatch(text) &&
        (text.Length <= 120 && SimpleMath.IsMatch(text) || MathOperator.IsMatch(text) && !ProseInMath.IsMatch(OutsideBraces(text)));
    private static bool PlausibleMath(string text) => !NumberProse.IsMatch(text) && !ProseInMath.IsMatch(OutsideBraces(text));
    private static bool LongPlainMath(string text) => text.Length > 24 && !EarlyMathOperator.IsMatch(text[..24]);
    private static int CodeLimit(string text, int start) { var next = IndexBefore(text, '`', start, Math.Min(text.Length, start + 81)); return next >= 0 ? next : text.Length; }
    private static int IndexBefore(string text, char value, int start, int end) { var found = text.IndexOf(value, Math.Min(start, text.Length)); return found >= 0 && found < end ? found : -1; }
    private static char At(string text, int position) => position >= 0 && position < text.Length ? text[position] : '\0';
    private static int RunLength(string text, int start) { var end = start + 1; while (At(text, end) == text[start]) end++; return end - start; }
    private static bool Escaped(string text, int position) { var count = 0; while (At(text, position - 1 - count) == '\\') count++; return count % 2 == 1; }
    private static int SurrogateSafe(string text, int offset) => offset > 0 && offset < text.Length && char.IsLowSurrogate(text[offset]) ? offset - 1 : offset;
    private static int FirstNonspace(string text) { for (var i = 0; i < text.Length; i++) if (!Whitespace(text[i])) return i; return -1; }
    private static bool Whitespace(char value) => value == '\ufeff' || value != '\u0085' && char.IsWhiteSpace(value);
    private static bool AsciiAlphaNumeric(char value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
    private static bool AsciiWord(char value) => AsciiAlphaNumeric(value) || value == '_';
    private static bool AsciiPunctuation(char value) => value is >= '!' and <= '/' or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~';
    private static bool WordOpening(char value) => char.IsLetterOrDigit(value) || char.GetUnicodeCategory(value) == UnicodeCategory.InitialQuotePunctuation || value is '"' or '\'' or '\u201e' or '\u201a' or '(' or '\u00bf' or '\u00a1';
    private static bool LatinOrGreek(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '\u00c0' and <= '\u02e4' or >= '\u0370' and <= '\u03ff' or >= '\u1e00' and <= '\u1fff';
    private static bool AllowedWord(string text) => text.EnumerateRunes().All(rune =>
    {
        var category = Rune.GetUnicodeCategory(rune);
        if (category is >= UnicodeCategory.NonSpacingMark and <= UnicodeCategory.OtherNumber or >= UnicodeCategory.ConnectorPunctuation and <= UnicodeCategory.OtherSymbol) return true;
        var value = rune.Value;
        return value is 0x200c or 0x200d or >= 0x0041 and <= 0x02e4 or >= 0x0370 and <= 0x052f or >= 0x0531 and <= 0x08ff or >= 0x0900 and <= 0x09ff or >= 0x10a0 and <= 0x11ff or >= 0x1e00 and <= 0x1fff or >= 0x3130 and <= 0x318f or >= 0xac00 and <= 0xd7ff;
    });
}
