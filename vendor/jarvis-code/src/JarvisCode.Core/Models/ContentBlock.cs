using System.Text.Json.Serialization;

namespace JarvisCode.Core.Models;

/// <summary>
/// One piece of message content. A message is an ordered list of blocks so that
/// text, tool calls and tool results can be interleaved the way every major
/// LLM API represents them.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextBlock), "text")]
[JsonDerivedType(typeof(ToolCallBlock), "tool_call")]
[JsonDerivedType(typeof(ToolResultBlock), "tool_result")]
[JsonDerivedType(typeof(ThinkingBlock), "thinking")]
[JsonDerivedType(typeof(RawProviderBlock), "raw")]
[JsonDerivedType(typeof(ImageBlock), "image")]
public abstract record ContentBlock;

/// <summary>An image the user attached, stored as base64 (e.g. image/png).</summary>
public sealed record ImageBlock(string MediaType, string Base64Data) : ContentBlock;

/// <summary>
/// Model reasoning. The signature is required to replay the block to Anthropic
/// in tool-use loops; without it the block is display-only.
/// </summary>
public sealed record ThinkingBlock(string Thinking, string? Signature) : ContentBlock
{
    /// <summary>
    /// Seconds this block spent streaming, when a client measured it. Written by
    /// the UI so a reloaded session can still say how long a message thought for;
    /// nothing in the engine reads it and no provider is shown it.
    /// </summary>
    public double? DurationSeconds { get; init; }
}

/// <summary>
/// An opaque vendor block (redacted thinking, server-side tool use/results) kept
/// verbatim so it can be replayed to the provider that produced it.
/// </summary>
public sealed record RawProviderBlock(string ProviderId, string RawJson) : ContentBlock;

/// <summary>Plain (markdown) text authored by the user or the model.</summary>
public sealed record TextBlock(string Text) : ContentBlock
{
    /// <summary>Source annotations received from the provider, preserved for display and replay.</summary>
    public IReadOnlyList<TextCitation>? Citations { get; init; }
}

/// <summary>The model requesting a tool invocation with JSON arguments.</summary>
public sealed record ToolCallBlock(string Id, string Name, string ArgumentsJson) : ContentBlock;

/// <summary>
/// The outcome of a tool invocation, fed back to the model. Images are
/// optional (screenshots from vision-carrying tools); older session files
/// simply deserialize them as null.
/// </summary>
public sealed record ToolResultBlock(
    string ToolCallId, string ToolName, string Content, bool IsError,
    IReadOnlyList<ImageBlock>? Images = null) : ContentBlock
{
    /// <summary>
    /// Extra text the tool injected alongside its result (a skill's loaded
    /// instructions). It becomes its own text block on the same user turn;
    /// older session files deserialize it as null.
    /// </summary>
    public string? FollowUpText { get; init; }

    /// <summary>
    /// Whether the permission gate refused this call, as opposed to the tool
    /// running and failing. It suppresses the batching reminder the way the
    /// reference's own refusal wordings do (see
    /// <see cref="Agent.ToolResultReminders.InterruptedPrefixes"/>), and it is
    /// carried structurally because a gate may supply any reason text — one of
    /// this build's opens with the tool's name, which no prefix can match.
    /// Older session files deserialize it as false.
    /// </summary>
    public bool Refused { get; init; }
}
