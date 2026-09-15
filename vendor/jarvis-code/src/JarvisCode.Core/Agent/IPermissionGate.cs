using System.Text.Json.Nodes;
using JarvisCode.Core.Tools;

namespace JarvisCode.Core.Agent;

public enum PermissionDecision
{
    Allow,
    /// <summary>Allow this call and stop asking for this tool for the rest of the session.</summary>
    AllowAlways,
    Deny,
}

/// <summary>
/// One pending tool call awaiting a user decision. <paramref name="CallId"/> is
/// the model's tool-call id when the request comes from a live turn, so hosts can
/// tie the prompt back to the transcript row; it is optional so hand-built
/// requests and older callers are unaffected.
/// </summary>
public sealed record PermissionRequest(
    ITool Tool, JsonObject Arguments, string CallDescription, string? CallId = null,
    string? HookPermissionDecision = null);

/// <summary>
/// Decides whether a mutating tool call may run. The hosting application asks
/// the user; tests supply a scripted implementation.
/// </summary>
public interface IPermissionGate
{
    ValueTask<PermissionDecision> RequestAsync(PermissionRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// A gate that can say <em>why</em> it denied a call. The orchestrator hands the
/// reason to the model as the tool result in place of its generic denial line —
/// which is how the reference's mode-specific refusals ("The user chose to block
/// reads outside the working directories…") reach it.
/// </summary>
public interface IDenialReasonSource
{
    /// <summary>The reason recorded for this call id, consumed on read; null when the denial was plain.</summary>
    string? TakeDenialReason(string? callId);

    /// <summary>
    /// Whether the denial recorded for this call id was the user's own answer —
    /// they were asked and said no — as opposed to a rule, a setting or a run
    /// with nobody to ask. Consumed on read alongside the reason.
    ///
    /// It decides whether the batching reminder stands down: the reference
    /// suppresses it for a user refusal or an interruption (each of its eight
    /// <c>e1o()</c> wordings is one of those) and <em>not</em> for a
    /// configuration denial — measured on CLI 2.1.257, where a settings deny
    /// rule produced "Permission to use Bash with command echo hi has been
    /// denied." and both reminders still rode the turn.
    /// </summary>
    bool TakeDenialWasUserRefusal(string? callId);
}

/// <summary>Approves everything — used for read-only tools and headless scenarios.</summary>
public sealed class AutoApprovePermissionGate : IPermissionGate
{
    public ValueTask<PermissionDecision> RequestAsync(PermissionRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(PermissionDecision.Allow);
}
