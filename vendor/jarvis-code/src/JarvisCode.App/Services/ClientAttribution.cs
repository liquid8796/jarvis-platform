using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.Core.Models;

namespace JarvisCode.App.Services;

/// <summary>
/// The request-identity fields the reference CLI attaches to every call — an
/// attribution line as the first system block, a <c>metadata.user_id</c> blob and a
/// session-id header — ported behind Settings › Fingerprints and off by default.
///
/// The mechanism is the reference's; the identity is this app's. The version token
/// names Jarvis Code, the salt below is our own, and the install id is generated
/// here rather than read from another client's config, so a request never claims to
/// come from a client that did not send it. The reference's remaining fields
/// (<c>cc_prev_req</c>, <c>cc_prompt_id</c>, <c>cch</c>) are first-party-only
/// bookkeeping with no counterpart here, and <c>cc_is_subagent</c> has none either:
/// subagent turns are built from Core's own prompt builder, which this block never
/// reaches.
/// </summary>
public static class ClientAttribution
{
    /// <summary>The reference's header-shaped prefix, which is what makes the line legible.</summary>
    private const string BlockPrefix = "x-anthropic-billing-header: ";

    /// <summary>
    /// Ours, not the reference's. Salting is what keeps the three published hex
    /// digits from being read straight back as the three prompt characters.
    /// </summary>
    private const string PromptFingerprintSalt = "6a1f9c40d3e7";

    /// <summary>Character positions of the first user message that the fingerprint samples.</summary>
    private static readonly int[] SampledPositions = [4, 7, 20];

    public const string SessionIdHeaderName = "X-Claude-Code-Session-Id";

    /// <summary>Product token, so the endpoint sees which client actually sent this.</summary>
    public const string Product = "jarvis-code";

    public static string Version =>
        typeof(ClientAttribution).Assembly.GetName().Version?.ToString(3) ?? "dev";

    /// <summary>
    /// The attribution line, to be carried as the first thing in the system prompt.
    /// <paramref name="entrypoint"/> names the surface that started the turn
    /// ("app", "cli"); <paramref name="firstUserText"/> is the conversation's first
    /// user-authored message, which is what keeps the fingerprint — and therefore
    /// the prompt cache — stable for the whole session.
    /// </summary>
    public static string Block(string entrypoint, string firstUserText)
    {
        var version = Version;
        return BlockPrefix +
               $"cc_version={Product}/{version}.{PromptFingerprint(firstUserText, version)}; " +
               $"cc_entrypoint={entrypoint};";
    }

    /// <summary>
    /// Three hex digits over three sampled characters of the first user message plus
    /// the client version — the reference's shape, at its 12 bits of resolution.
    /// Absent characters read as '0', so a message shorter than the last sampled
    /// position still produces a fingerprint.
    /// </summary>
    internal static string PromptFingerprint(string firstUserText, string version)
    {
        var sampled = new StringBuilder(SampledPositions.Length);
        foreach (var position in SampledPositions)
        {
            sampled.Append(position < firstUserText.Length ? firstUserText[position] : '0');
        }

        var digest = SHA256.HashData(
            Encoding.UTF8.GetBytes(PromptFingerprintSalt + sampled + version));
        return Convert.ToHexString(digest).ToLowerInvariant()[..3];
    }

    /// <summary>
    /// The first user-authored text of a conversation. Messages the harness composed
    /// end to end (task notifications) are skipped, the way the reference skips its
    /// own meta messages; an empty conversation reads as empty text.
    /// </summary>
    public static string FirstUserText(IReadOnlyList<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            if (message.Role != Role.User)
            {
                continue;
            }

            var text = SystemReminders.VisibleText(message);
            if (text.Length > 0)
            {
                return text;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// The reference's <c>metadata.user_id</c>: a JSON object, stringified, naming
    /// the install and the session. The account field is present but empty — there
    /// is no account concept here, and the reference sends it empty in that case too.
    /// </summary>
    public static JsonObject MetadataPatch(string deviceId, string sessionId) =>
        new()
        {
            ["metadata"] = new JsonObject
            {
                ["user_id"] = JsonSerializer.Serialize(new JsonObject
                {
                    ["device_id"] = deviceId,
                    ["account_uuid"] = string.Empty,
                    ["session_id"] = sessionId,
                }),
            },
        };

    /// <summary>
    /// This install's pseudonymous id, generated on first use and kept in
    /// ui-settings. The caller saves the settings; a malformed stored value is
    /// replaced rather than sent.
    /// </summary>
    public static string EnsureDeviceId(UiSettings settings)
    {
        if (settings.ClientDeviceId is { Length: 64 } stored && stored.All(Uri.IsHexDigit))
        {
            return stored;
        }

        settings.ClientDeviceId =
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        return settings.ClientDeviceId;
    }
}
