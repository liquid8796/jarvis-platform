using System.Text.Json.Nodes;

namespace JarvisCode.Core.Providers;

/// <summary>
/// The HTTP call a provider would make for one <see cref="LlmRequest"/>: what the
/// request inspector renders. Header values that carry a credential are redacted by
/// the provider that builds the preview — the real key never reaches this record.
/// </summary>
public sealed record ProviderRequestPreview(
    string Method,
    string Url,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    JsonObject Body);

/// <summary>
/// Optional provider capability: build the request that <em>would</em> be sent,
/// without sending it. Implementations delegate to their own body builder, so a
/// preview cannot drift from the wire. Providers whose transport has no JSON
/// payload — the ChatGPT web session types into a page — do not implement it.
/// </summary>
public interface IRequestInspector
{
    /// <summary>
    /// The call <paramref name="request"/> produces, with its
    /// <see cref="LlmRequest.BodyOverride"/> already applied. Pass a request with no
    /// override to get the untouched body to diff hand edits against.
    /// </summary>
    ProviderRequestPreview PreviewRequest(LlmRequest request);
}

/// <summary>
/// Hand edits to a provider's request body, carried as an RFC 7386 JSON merge patch
/// rather than a frozen body: one turn makes many model calls and the message list
/// grows between them, so only a patch can survive the loop. A key mapped to JSON
/// null removes it — which also means an explicitly typed null cannot be told apart
/// from a deletion, the one ambiguity the format has.
/// </summary>
public static class RequestBodyOverride
{
    /// <summary>Stands in for a credential in a preview's headers.</summary>
    public const string RedactedValue = "••••••••";

    /// <summary>
    /// Applies <paramref name="patch"/> to <paramref name="body"/> in place and
    /// returns it. A null patch — the default everywhere — leaves the body exactly
    /// as the adapter built it.
    /// </summary>
    public static JsonObject Apply(JsonObject body, JsonObject? patch)
    {
        if (patch is null)
        {
            return body;
        }

        foreach (var (key, value) in patch)
        {
            switch (value)
            {
                case null:
                    body.Remove(key);
                    break;
                case JsonObject nested when body[key] is JsonObject target:
                    Apply(target, nested);
                    break;
                case JsonObject nested:
                    // Patching a non-object: RFC 7386 says apply to an empty object,
                    // which drops the nested removals instead of writing nulls out.
                    body[key] = Apply(new JsonObject(), nested);
                    break;
                default:
                    body[key] = value.DeepClone();
                    break;
            }
        }

        return body;
    }

    /// <summary>
    /// The merge patch that turns <paramref name="baseline"/> into
    /// <paramref name="edited"/>, or null when they already agree. Only the keys the
    /// user actually changed end up in it, so everything else keeps tracking the
    /// adapter — a later model or effort change still reaches the wire.
    /// </summary>
    public static JsonObject? Diff(JsonObject baseline, JsonObject edited)
    {
        var patch = new JsonObject();

        foreach (var (key, editedValue) in edited)
        {
            if (!baseline.TryGetPropertyValue(key, out var baseValue))
            {
                patch[key] = editedValue?.DeepClone();
                continue;
            }

            if (baseValue is JsonObject baseObject && editedValue is JsonObject editedObject)
            {
                if (Diff(baseObject, editedObject) is { } nested)
                {
                    patch[key] = nested;
                }

                continue;
            }

            if (!JsonNode.DeepEquals(baseValue, editedValue))
            {
                patch[key] = editedValue?.DeepClone();
            }
        }

        foreach (var (key, _) in baseline)
        {
            if (!edited.ContainsKey(key))
            {
                patch[key] = null;
            }
        }

        return patch.Count == 0 ? null : patch;
    }

    /// <summary>
    /// The top-level keys a patch touches, for telling the user what is pinned.
    /// Nested edits are reported under their top-level key.
    /// </summary>
    public static IReadOnlyList<string> TouchedKeys(JsonObject? patch) =>
        patch is null ? [] : [.. patch.Select(entry => entry.Key).OrderBy(static key => key, StringComparer.Ordinal)];
}
