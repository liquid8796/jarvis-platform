namespace JarvisCode.Core.Settings;

/// <summary>
/// A provider the user defined by hand: an endpoint that speaks a protocol this app
/// already implements, under an id of their own. Everything else about it — the key
/// slot, the model list, the context overrides — is keyed by <see cref="Id"/> and so
/// works exactly as it does for a built-in provider.
/// </summary>
public sealed class CustomProviderSpec
{
    /// <summary>
    /// The provider id, which is what keys, models and sessions are stored against.
    /// Immutable in practice: editing it would orphan everything already filed under
    /// the old one, so the card offers removal and re-adding instead.
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>What the model menu and the provider card call it.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>The API root; the protocol below decides which path is appended to it.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// <see cref="CustomProviderProtocols.OpenAi"/> or
    /// <see cref="CustomProviderProtocols.Anthropic"/>; anything else reads as OpenAI.
    /// </summary>
    public string ProtocolName { get; set; } = CustomProviderProtocols.OpenAi;

    /// <summary>
    /// False for an endpoint that serves unauthenticated requests — a model server on
    /// the local network — so it never fails with a missing-key error it does not need.
    /// </summary>
    public bool RequiresApiKey { get; set; } = true;
}

/// <summary>The wire formats a custom provider may be pointed at.</summary>
public static class CustomProviderProtocols
{
    /// <summary>Chat completions: <c>{base}/chat/completions</c>.</summary>
    public const string OpenAi = "OpenAI";

    /// <summary>Messages: <c>{base}/v1/messages</c>.</summary>
    public const string Anthropic = "Anthropic";

    public static readonly IReadOnlyList<string> All = [OpenAi, Anthropic];

    /// <summary>Unknown values read as OpenAI, which is what an unlabelled relay usually is.</summary>
    public static bool IsAnthropic(string? protocolName) =>
        string.Equals(protocolName, Anthropic, StringComparison.OrdinalIgnoreCase);

    /// <summary>The stored spelling for a value that may have been typed or migrated.</summary>
    public static string Normalize(string? protocolName) =>
        IsAnthropic(protocolName) ? Anthropic : OpenAi;
}

/// <summary>
/// The rules a hand-written provider has to satisfy before it is stored. Pure so the
/// card can say what is wrong while the user is still typing.
/// </summary>
public static class CustomProviders
{
    /// <summary>
    /// Long enough for a descriptive name, short enough that the id stays readable in
    /// the settings file and on a card.
    /// </summary>
    public const int MaxIdLength = 40;

    /// <summary>
    /// Ids are lowercased and trimmed, because everything that looks one up does so
    /// case-insensitively and two entries differing only in case would be one provider
    /// with two cards.
    /// </summary>
    public static string NormalizeId(string? id) => (id ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// Why this id cannot be used, or null when it can. <paramref name="takenIds"/> is
    /// every id already spoken for — the built-in providers and the other custom ones —
    /// because a collision would shadow one of them rather than add anything.
    /// </summary>
    public static string? DescribeIdProblem(string? id, IEnumerable<string> takenIds)
    {
        var normalized = NormalizeId(id);
        if (normalized.Length == 0)
            return "Give the provider an id — it is what keys and models are stored against.";
        if (normalized.Length > MaxIdLength)
            return $"Ids are at most {MaxIdLength} characters.";
        if (!char.IsAsciiLetterOrDigit(normalized[0]))
            return "An id starts with a letter or a digit.";
        if (!normalized.All(IsIdCharacter))
            return "An id may only hold letters, digits, hyphens, underscores and dots.";
        return takenIds.Any(taken => NormalizeId(taken) == normalized)
            ? $"“{normalized}” is already taken by another provider. Pick a different id."
            : null;
    }

    /// <summary>
    /// Why this base URL cannot be used, or null when it can. A relative or scheme-less
    /// value is refused rather than guessed at: the endpoint decides where a key is sent.
    /// </summary>
    public static string? DescribeBaseUrlProblem(string? baseUrl)
    {
        var trimmed = (baseUrl ?? "").Trim();
        if (trimmed.Length == 0)
            return "Give the endpoint's base URL, e.g. https://api.example.com/v1.";
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? null
            : "That is not a full http:// or https:// URL.";
    }

    /// <summary>
    /// The spec as it will be stored: id and protocol normalized, URL trimmed, and a
    /// blank display name filled in from the id so nothing renders nameless.
    /// </summary>
    public static CustomProviderSpec Normalize(CustomProviderSpec spec)
    {
        var id = NormalizeId(spec.Id);
        var name = (spec.DisplayName ?? "").Trim();
        return new CustomProviderSpec
        {
            Id = id,
            DisplayName = name.Length > 0 ? name : id,
            BaseUrl = (spec.BaseUrl ?? "").Trim(),
            ProtocolName = CustomProviderProtocols.Normalize(spec.ProtocolName),
            RequiresApiKey = spec.RequiresApiKey,
        };
    }

    /// <summary>
    /// The stored providers that are actually usable: an entry whose id or URL is
    /// unusable is skipped rather than registered, so a hand-edited settings file
    /// cannot produce a provider that fails on every call. Later duplicates of an id
    /// lose to the first, matching how the model catalog resolves its own collisions.
    /// </summary>
    public static IReadOnlyList<CustomProviderSpec> Usable(
        IEnumerable<CustomProviderSpec> specs, IEnumerable<string> reservedIds)
    {
        var taken = new HashSet<string>(reservedIds.Select(NormalizeId), StringComparer.Ordinal);
        var usable = new List<CustomProviderSpec>();
        foreach (var spec in specs)
        {
            if (DescribeIdProblem(spec.Id, taken) is not null ||
                DescribeBaseUrlProblem(spec.BaseUrl) is not null)
            {
                continue;
            }

            var normalized = Normalize(spec);
            taken.Add(normalized.Id);
            usable.Add(normalized);
        }

        return usable;
    }

    private static bool IsIdCharacter(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.';
}
