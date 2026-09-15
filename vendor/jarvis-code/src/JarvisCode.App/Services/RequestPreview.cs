using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.App.Composition;
using JarvisCode.App.ViewModels;
using JarvisCode.Core.Providers;

namespace JarvisCode.App.Services;

/// <summary>
/// What the request inspector renders: the call the session's next model turn would
/// make, plus the untouched body to diff hand edits against. <see cref="Unavailable"/>
/// is set instead of the rest when there is nothing to show.
/// </summary>
public sealed record RequestPreview(
    string ProviderName,
    string Method,
    string Url,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    JsonObject Baseline,
    string BaselineJson,
    string EffectiveJson,
    IReadOnlyList<string> OverriddenKeys,
    string? Unavailable)
{
    public bool HasOverride => OverriddenKeys.Count > 0;

    public static RequestPreview NotAvailable(string reason) =>
        new("", "", "", [], new JsonObject(), "", "", [], reason);
}

/// <summary>
/// Builds the request the next model call would send, through the same turn factory
/// and the same provider body builder the send path uses — a reconstruction would
/// drift from the wire the first time either changed.
/// </summary>
public static class RequestPreviewBuilder
{
    /// <summary>
    /// Payload keys whose meaning the agent loop depends on. Pinning one is allowed —
    /// the point of the editor — but the user is told what it costs.
    /// </summary>
    private static readonly HashSet<string> LoopCriticalKeys =
        new(StringComparer.Ordinal) { "messages", "contents", "stream", "model", "tools" };

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        // The wire escapes non-ASCII; a reader editing a Vietnamese conversation
        // should not have to decode \uXXXX to find their own text. Same values,
        // different escaping.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Format(JsonNode node) => node.ToJsonString(Pretty);

    /// <summary>The loop-critical keys this patch pins, in the order they appear.</summary>
    public static IReadOnlyList<string> CriticalKeys(IEnumerable<string> overriddenKeys) =>
        [.. overriddenKeys.Where(LoopCriticalKeys.Contains)];

    public static RequestPreview Build(AppServices services, ChatViewModel viewModel)
    {
        var session = viewModel.Session;
        var factory = new TurnContextFactory(services);
        if (factory.ResolveModel(session) is not { } model)
        {
            return RequestPreview.NotAvailable(
                "No model is configured. Add an API key and pick a model in Settings, then reopen this.");
        }

        var provider = services.Providers.Get(model.ProviderId);
        if (ProviderDecorators.Unwrap(provider) is not IRequestInspector inspector)
        {
            return RequestPreview.NotAvailable(
                $"{provider.DisplayName} sends no JSON request — this session drives a web page instead of an " +
                "API, so there is no payload to inspect or edit.");
        }

        // Assembling a turn writes the permission gate's rule lines and hook. A
        // preview must leave a turn that is already running exactly as it found it,
        // so both are put back before returning.
        var savedRules = viewModel.Gate.ExtraRuleLines;
        var savedHook = viewModel.Gate.PermissionRequestHookAsync;
        TurnSetup? setup;
        try
        {
            // The same call the next turn makes. It reads plugins, skills and hooks
            // off disk; the checkpoint it returns is an in-memory record that writes
            // nothing until a tool actually captures a file.
            setup = viewModel.IsCodeSurface
                ? factory.CreateForCode(
                    session, viewModel.Gate, userPrompt: "", todoSink: null, subagentActivity: null,
                    extraTools: viewModel.ExtraTools, lastContextTokens: viewModel.LastContextTokens,
                    askUser: null, planApproval: null, workers: viewModel.Workers,
                    coordinatorMode: viewModel.CoordinatorMode, memoryPaused: viewModel.MemoryPaused,
                    ultracodeMode: viewModel.UltracodeMode)
                : factory.CreateForChat(session, viewModel.Gate, viewModel.ExtraTools);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RequestPreview.NotAvailable($"Could not assemble the request: {ex.Message}");
        }
        finally
        {
            viewModel.Gate.ExtraRuleLines = savedRules;
            viewModel.Gate.PermissionRequestHookAsync = savedHook;
        }

        if (setup is null)
        {
            return RequestPreview.NotAvailable("No model is configured for this session.");
        }

        var request = setup.Context.ToRequest();
        var patch = RequestOverrides.For(session.Id);
        var baseline = inspector.PreviewRequest(request with { BodyOverride = null });
        var effective = patch is null
            ? baseline
            : inspector.PreviewRequest(request with { BodyOverride = patch });

        return new RequestPreview(
            provider.DisplayName,
            baseline.Method,
            baseline.Url,
            baseline.Headers,
            baseline.Body,
            Format(baseline.Body),
            Format(effective.Body),
            RequestBodyOverride.TouchedKeys(patch),
            Unavailable: null);
    }
}
