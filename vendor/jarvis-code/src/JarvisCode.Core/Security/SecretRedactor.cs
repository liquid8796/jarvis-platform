using System.Text.RegularExpressions;

namespace JarvisCode.Core.Security;

/// <summary>
/// Scrubs API keys and bearer tokens from persisted text (ported from claw-code's
/// transcript redaction): shell outputs routinely leak env dumps into tool results,
/// and session files should be safe to share. Patterns are chosen so they can never
/// match inside base64 payloads (every pattern requires a character outside the
/// base64 alphabet, such as '-', '_' or whitespace), keeping stored images intact.
/// </summary>
public static class SecretRedactor
{
    public const string Replacement = "[redacted]";

    private static readonly Regex[] Patterns =
    [
        // Anthropic / OpenAI style keys: sk-ant-…, sk-proj-…, sk-…
        new(@"(?<![A-Za-z0-9])sk-[A-Za-z0-9_-]{16,}(?![A-Za-z0-9_-])", RegexOptions.Compiled),
        // GitHub tokens: ghp_/gho_/ghu_/ghs_/ghr_ + 36 chars, and fine-grained github_pat_
        new(@"(?<![A-Za-z0-9])gh[porus]_[A-Za-z0-9]{20,}(?![A-Za-z0-9_-])", RegexOptions.Compiled),
        new(@"(?<![A-Za-z0-9])github_pat_[A-Za-z0-9_]{20,}(?![A-Za-z0-9_-])", RegexOptions.Compiled),
        // Authorization headers: "Authorization: Bearer x", "Bearer x" (whitespace required)
        new(@"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{16,}", RegexOptions.Compiled),
        // KEY=value env dumps for well-known key names. The value class excludes
        // quotes and backslashes so redaction inside serialized JSON can never eat
        // a string delimiter and corrupt the document.
        new("""(?i)\b((?:ANTHROPIC|OPENAI|GEMINI|GOOGLE|DASHSCOPE|XAI|OLLAMA|OPENROUTER|AZURE)[A-Z_]*?(?:KEY|TOKEN))\s*=\s*[^\s"\\]+""",
            RegexOptions.Compiled),
    ];

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        foreach (var pattern in Patterns)
        {
            text = pattern.Replace(text, match =>
                match.Groups.Count > 1 && match.Groups[1].Success
                    ? $"{match.Groups[1].Value}={Replacement}"
                    : Replacement);
        }
        return text;
    }

    /// <summary>True when the text contains something the redactor would scrub.</summary>
    public static bool ContainsSecret(string text) =>
        !string.IsNullOrEmpty(text) && Patterns.Any(pattern => pattern.IsMatch(text));
}
