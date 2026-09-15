namespace JarvisCode.App.Services;

/// <summary>
/// The product-identity sentence that opens the system prompt, ahead of the
/// harness block.
/// </summary>
/// <remarks>
/// The reference sends one of three sentences here, chosen by entrypoint
/// (CLI 2.1.251, the picker its bundle spells <c>cX</c>):
///
/// <list type="bullet">
/// <item>the plain "official CLI" line in an interactive session, and on Vertex
/// whatever else is true;</item>
/// <item>the same line plus a "running within the Claude Agent SDK" clause in a
/// non-interactive session that was handed an appended system prompt — which is
/// how the desktop drives it;</item>
/// <item>a bare "You are a Claude agent, built on Anthropic's Claude Agent SDK."
/// in any other non-interactive session, which is what a plain <c>-p</c> run
/// records.</item>
/// </list>
///
/// This port carries the <em>mechanism</em> and not the text, which is a
/// deliberate split. The sentence is an attribution claim rather than protocol:
/// its whole function is to tell the other end which product is calling, and
/// the same reasoning already settled <see cref="ClientAttribution"/> — a
/// request must not claim to have come from a client that did not send it.
/// Copying it verbatim would have this app assert it is Anthropic's official
/// CLI, which is false however the prompt is worded.
///
/// The three variants also collapse here, and saying so is more honest than
/// shipping two that cannot be true: two of them describe a session running
/// inside the Claude Agent SDK, and this app has no SDK to run inside. What is
/// left is one sentence naming the product that is really calling.
///
/// What this port does <em>not</em> reproduce is the wire shape. The reference
/// hoists the identity into a system block of its own with an org-scoped cache
/// control, so it caches separately from the harness block below it; the
/// engine's <c>AgentTurnContext.SystemPrompt</c> is a single string, and
/// splitting it is a provider-contract change rather than an additive one. The
/// sentence therefore rides in the same block, in the same position. Both
/// deltas are declared in the parity manifests.
/// </remarks>
internal static class PromptIdentity
{
    /// <summary>
    /// The sentence this app opens with. It names the product that is actually
    /// making the request, which is the one thing the reference's own line is
    /// for.
    /// </summary>
    internal const string Sentence = "You are Jarvis Code, an interactive coding assistant.";
}
