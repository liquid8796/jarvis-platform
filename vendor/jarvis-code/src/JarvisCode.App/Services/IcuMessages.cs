using System.Globalization;
using System.Text;

namespace JarvisCode.App.Services;

/// <summary>
/// The slice of ICU MessageFormat the reference's routines strings use:
/// <c>{name}</c> substitution, <c>{n, plural, =0 {…} one {…} other {…}}</c> with
/// <c>#</c> for the count, and <c>{x, select, yes {…} other {…}}</c>. Kept small on
/// purpose — the catalogue strings are pinned by the parity suite as the ICU
/// literals the reference ships, so the app has to carry those literals and
/// render them itself rather than restate them as C# interpolations.
/// </summary>
public static class IcuMessages
{
    /// <summary>Renders <paramref name="message"/> with the named arguments.</summary>
    public static string Format(string message, IReadOnlyDictionary<string, object?> args)
    {
        var text = new StringBuilder();
        var i = 0;
        while (i < message.Length)
        {
            var c = message[i];
            if (c != '{')
            {
                text.Append(c);
                i++;
                continue;
            }

            var close = MatchingBrace(message, i);
            var body = message[(i + 1)..close];
            text.Append(RenderArgument(body, args));
            i = close + 1;
        }

        return text.ToString();
    }

    public static string Format(string message, string name, object? value) =>
        Format(message, new Dictionary<string, object?> { [name] = value });

    public static string Format(string message, string name1, object? value1, string name2, object? value2) =>
        Format(message, new Dictionary<string, object?> { [name1] = value1, [name2] = value2 });

    private static string RenderArgument(string body, IReadOnlyDictionary<string, object?> args)
    {
        var comma = body.IndexOf(',');
        if (comma < 0)
        {
            var name = body.Trim();
            return args.TryGetValue(name, out var plain) ? Convert.ToString(plain, CultureInfo.CurrentCulture) ?? "" : "";
        }

        var argument = body[..comma].Trim();
        var rest = body[(comma + 1)..];
        var comma2 = rest.IndexOf(',');
        var kind = (comma2 < 0 ? rest : rest[..comma2]).Trim();
        var options = comma2 < 0 ? "" : rest[(comma2 + 1)..];
        args.TryGetValue(argument, out var value);

        var branches = ParseBranches(options);
        if (kind == "plural")
        {
            var count = value is null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
            var chosen = branches.TryGetValue($"={count}", out var exact) ? exact
                : count == 1 && branches.TryGetValue("one", out var one) ? one
                : branches.TryGetValue("other", out var other) ? other
                : "";
            return Format(chosen.Replace("#", count.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal), args);
        }

        if (kind == "select")
        {
            var key = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            var chosen = branches.TryGetValue(key, out var match) ? match
                : branches.TryGetValue("other", out var other) ? other
                : "";
            return Format(chosen, args);
        }

        return Convert.ToString(value, CultureInfo.CurrentCulture) ?? "";
    }

    private static Dictionary<string, string> ParseBranches(string options)
    {
        var branches = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = 0;
        while (i < options.Length)
        {
            while (i < options.Length && char.IsWhiteSpace(options[i]))
            {
                i++;
            }

            var start = i;
            while (i < options.Length && options[i] != '{' && !char.IsWhiteSpace(options[i]))
            {
                i++;
            }

            var key = options[start..i];
            while (i < options.Length && options[i] != '{')
            {
                i++;
            }

            if (i >= options.Length)
            {
                break;
            }

            var close = MatchingBrace(options, i);
            branches[key] = options[(i + 1)..close];
            i = close + 1;
        }

        return branches;
    }

    private static int MatchingBrace(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}' && --depth == 0)
            {
                return i;
            }
        }

        return text.Length - 1;
    }
}
