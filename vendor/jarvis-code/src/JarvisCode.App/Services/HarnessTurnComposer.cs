using System.Collections.Generic;
using System.Linq;
using JarvisCode.Core.Models;
using JarvisCode.Providers.Anthropic;

namespace JarvisCode.App.Services;

/// <summary>
/// Decides how the harness's own sections — roster, skill listing, plan and
/// mode notices, the token block — reach the model on a user turn: as the
/// reference's trailing <c>role: system</c> message for a model that has the
/// mid-conversation system role, or as <c>&lt;system-reminder&gt;</c> blocks
/// leading the user message for one that does not.
/// </summary>
/// <remarks>
/// Measured on CLI 2.1.257, one capture per model. opus-5, opus-4-8, sonnet-5
/// and the fables send <c>messages: [user[context, prompt], system[roster,
/// skills, plan, total_tokens]]</c>. opus-4-6, opus-4-5, sonnet-4.x, haiku-4-5
/// and the Claude 3 generation send a single user message whose blocks are, in
/// order, the roster, the skill listing, the plan reminder and the token block —
/// each wrapped in reminder tags — then the context reminder and the prompt.
/// The gate is the model's <c>mid-conversation-system</c> beta
/// (<see cref="AnthropicEffort.SupportsHarnessSystemTurn"/>); a model on another
/// wire never has the role and takes the reminder shape, which is also what the
/// reference would send it.
/// </remarks>
internal static class HarnessTurnComposer
{
    /// <summary>True when the model takes the harness's sections as a system turn.</summary>
    public static bool UsesSystemTurn(string modelId) => AnthropicEffort.SupportsHarnessSystemTurn(modelId);

    /// <summary>
    /// Places the sections. For a system-turn model the user message is returned
    /// untouched and the sections come back joined as the system message's body;
    /// otherwise each section is prepended to the user message as its own
    /// reminder block, in order, and no body is returned.
    /// </summary>
    /// <param name="sections">
    /// In the order they should read, top to bottom. The token block, when
    /// present, is the last of them and carries the reference's trailing newline
    /// on the reminder path.
    /// </param>
    public static (ChatMessage UserMessage, string? SystemTurnBody) Place(
        string modelId, ChatMessage userMessage, IReadOnlyList<string> sections)
    {
        var present = sections.Where(static s => !string.IsNullOrEmpty(s)).ToList();
        if (present.Count == 0)
        {
            return (userMessage, null);
        }

        if (UsesSystemTurn(modelId))
        {
            return (userMessage, string.Join("\n\n", present));
        }

        // Measured on CLI 2.1.257 (opus-4-5, haiku-4-5): the first user message
        // is [roster, skills, plan workflow, token budget, context reminder,
        // prompt] — the harness sections lead, in that order, and the context
        // reminder that was attached earlier follows them. Lead inserts at the
        // front, so the last section goes in first.
        for (var i = present.Count - 1; i >= 0; i--)
        {
            var wrapped = SystemReminders.WrapReminder(present[i]);
            if (present[i].StartsWith("<total_tokens>", System.StringComparison.Ordinal))
            {
                // The captured token block alone carries a newline after its
                // closing tag (87 bytes against the 86 the others would give).
                wrapped += "\n";
            }

            userMessage = SystemReminders.Lead(userMessage, wrapped);
        }

        return (userMessage, null);
    }
}
