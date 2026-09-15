using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JarvisCode.Providers.ChatGptWeb;

/// <summary>One cookie from a browser export, with the fields a browser needs to accept it back.</summary>
public sealed record ChatGptCookie
{
    public required string Name { get; init; }
    public string Value { get; init; } = "";
    public string Domain { get; init; } = DefaultDomain;
    public string Path { get; init; } = "/";
    public bool Secure { get; init; }
    public bool HttpOnly { get; init; }

    /// <summary>Raw export string ("lax" | "strict" | "no_restriction" | …), mapped by the transport.</summary>
    public string? SameSite { get; init; }

    /// <summary>Unix seconds; 0 means a session cookie with no expiry.</summary>
    public double ExpirationDate { get; init; }

    public const string DefaultDomain = ".chatgpt.com";
}

/// <summary>
/// The ChatGPT session cookies a user exported from their own browser, parsed from whichever
/// shape the common extensions produce.
///
/// Domain, path, secure, httpOnly, sameSite and expiry are all kept: installing cookies as a flat
/// "name=value" list loses the scoping that makes the browser send them back, and the session then
/// silently fails to authenticate. Parsing never throws — a malformed export yields an unusable jar
/// whose <see cref="Describe"/> says what is missing.
/// </summary>
public sealed class ChatGptCookieJar
{
    /// <summary>
    /// The cookie that actually carries the web session. NextAuth splits large tokens into
    /// ".0"/".1" chunks, so every cookie whose name starts with this counts.
    /// </summary>
    public const string SessionTokenName = "__Secure-next-auth.session-token";

    private readonly List<ChatGptCookie> _cookies;

    private ChatGptCookieJar(List<ChatGptCookie> cookies) => _cookies = cookies;

    public IReadOnlyList<ChatGptCookie> Cookies => _cookies;

    public int Count => _cookies.Count;

    public bool HasSessionToken => _cookies.Any(c =>
        c.Name.StartsWith(SessionTokenName, StringComparison.Ordinal) && c.Value.Length > 0);

    /// <summary>Whether a sign-in attempt is worth making at all.</summary>
    public bool IsUsable => _cookies.Count > 0 && HasSessionToken;

    public static ChatGptCookieJar Empty() => new([]);

    /// <summary>Parses any supported export shape; returns an empty jar rather than throwing.</summary>
    public static ChatGptCookieJar Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Empty();
        }

        var text = raw.Trim();
        try
        {
            if (text.StartsWith('{') || text.StartsWith('['))
            {
                return ParseJson(text);
            }

            return LooksLikeNetscape(text) ? ParseNetscape(text) : ParseHeaderLine(text);
        }
        catch (JsonException)
        {
            // A JSON-looking export that will not parse still often carries a usable header line.
            return ParseHeaderLine(text);
        }
    }

    /// <summary>A "name=value; …" line — only useful for the plain-HTTP fallback transport.</summary>
    public string ToHeader()
    {
        var builder = new StringBuilder();
        foreach (var cookie in _cookies)
        {
            if (builder.Length > 0)
            {
                builder.Append("; ");
            }

            builder.Append(cookie.Name).Append('=').Append(cookie.Value);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The jar as JSON for the browser transport to install with full scoping. Values travel only
    /// as far as the local browser engine and are never logged.
    /// </summary>
    public string ToInjectionJson()
    {
        var array = new JsonArray();
        foreach (var cookie in _cookies)
        {
            array.Add(new JsonObject
            {
                ["name"] = cookie.Name,
                ["value"] = cookie.Value,
                ["domain"] = cookie.Domain,
                ["path"] = cookie.Path,
                ["secure"] = cookie.Secure,
                ["httpOnly"] = cookie.HttpOnly,
                ["sameSite"] = cookie.SameSite ?? "",
                ["expirationDate"] = cookie.ExpirationDate,
            });
        }

        return array.ToJsonString();
    }

    /// <summary>A value-free line for the settings page — never quotes a cookie.</summary>
    public string Describe() => _cookies.Count switch
    {
        0 => "No cookies parsed. Export them from a signed-in chatgpt.com tab.",
        _ => HasSessionToken
            ? $"{_cookies.Count} cookies, session token present."
            : $"{_cookies.Count} cookies, but the {SessionTokenName} cookie is missing — sign-in will fail.",
    };

    private static ChatGptCookieJar ParseJson(string text)
    {
        var root = JsonNode.Parse(text);
        var array = root switch
        {
            JsonArray direct => direct,
            // Cookie-Editor writes { "url": …, "cookies": [ … ] }.
            JsonObject obj when obj["cookies"] is JsonArray nested => nested,
            JsonObject obj when obj["name"] is not null => [obj.DeepClone()],
            _ => [],
        };

        var cookies = new List<ChatGptCookie>();
        foreach (var item in array)
        {
            if (item is not JsonObject cookie)
            {
                continue;
            }

            var name = Text(cookie, "name");
            if (name.Length == 0)
            {
                continue;
            }

            cookies.Add(new ChatGptCookie
            {
                Name = name,
                Value = Text(cookie, "value"),
                Domain = Text(cookie, "domain") is { Length: > 0 } domain ? domain : ChatGptCookie.DefaultDomain,
                Path = Text(cookie, "path") is { Length: > 0 } path ? path : "/",
                Secure = Flag(cookie, "secure"),
                HttpOnly = Flag(cookie, "httpOnly"),
                SameSite = Text(cookie, "sameSite") is { Length: > 0 } sameSite ? sameSite : null,
                ExpirationDate = Number(cookie, "expirationDate", "expires", "expiry"),
            });
        }

        return new ChatGptCookieJar(cookies);
    }

    private static ChatGptCookieJar ParseHeaderLine(string text)
    {
        var cookies = new List<ChatGptCookie>();
        foreach (var part in text.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            cookies.Add(new ChatGptCookie
            {
                Name = part[..separator].Trim(),
                Value = part[(separator + 1)..].Trim(),
                Secure = true,
            });
        }

        return new ChatGptCookieJar(cookies);
    }

    // A cookies.txt file is tab separated; a header line has no tabs at all.
    private static bool LooksLikeNetscape(string text) => text.Contains('\t');

    private static ChatGptCookieJar ParseNetscape(string text)
    {
        var cookies = new List<ChatGptCookie>();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            // domain · includeSubdomains · path · secure · expiry · name · value
            var fields = trimmed.Split('\t');
            if (fields.Length < 7 || fields[5].Length == 0)
            {
                continue;
            }

            double.TryParse(fields[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var expiry);
            cookies.Add(new ChatGptCookie
            {
                Domain = fields[0].Length > 0 ? fields[0] : ChatGptCookie.DefaultDomain,
                Path = fields[2].Length > 0 ? fields[2] : "/",
                Secure = string.Equals(fields[3], "TRUE", StringComparison.OrdinalIgnoreCase),
                ExpirationDate = expiry,
                Name = fields[5],
                Value = fields[6],
            });
        }

        return new ChatGptCookieJar(cookies);
    }

    private static string Text(JsonObject cookie, string name) =>
        (cookie[name] ?? cookie[Capitalize(name)]) is JsonValue value && value.TryGetValue<string>(out var text)
            ? text.Trim()
            : "";

    private static bool Flag(JsonObject cookie, string name)
    {
        var node = cookie[name] ?? cookie[Capitalize(name)];
        return node is JsonValue value
            && (value.TryGetValue<bool>(out var flag) ? flag
                : value.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed) && parsed);
    }

    private static double Number(JsonObject cookie, params string[] names)
    {
        foreach (var name in names)
        {
            if (cookie[name] is not JsonValue value)
            {
                continue;
            }

            if (value.TryGetValue<double>(out var number))
            {
                return number;
            }

            if (value.TryGetValue<string>(out var text)
                && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return 0;
    }

    private static string Capitalize(string name) => char.ToUpperInvariant(name[0]) + name[1..];
}
