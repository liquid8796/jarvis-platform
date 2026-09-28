using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Protocol;
using JarvisCode.Core.Mcp;

namespace Jarvis.Agent.Windows;

/// <summary>Observational parity audit, not a claim that credentials/quota or a scene are ready.</summary>
public static class BlenderMcpCapabilities
{
    public const string UpstreamVersion = "2.1.1";
    public const string UpstreamCommit = "41a184322db3ccdcb2fdbc1f6994afe71bf9163c";
    public static readonly ToolDescriptor Descriptor = new("blender.get_capabilities", "blender_get_capabilities", "blender",
        "Audit Blender's live tool catalog against the reviewed upstream 2.1.1 baseline. Optional probeAddon reads the addon handshake. " +
        "Does not enable providers, spend credits, install anything or mutate a scene. Safe Mode and local approval still apply. " +
        "Catalog parity and provider readiness are different; check individual get_*_status tools before provider use.",
        JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","additionalProperties":false,"properties":{"probeAddon":{"type":"boolean","default":false}}}
        """), ReadOnly: false, Sensitive: true);

    public static readonly IReadOnlyDictionary<string, string[]> Groups = new Dictionary<string, string[]>
    {
        ["core"] = ["get_addon_status", "disable_telemetry", "get_scene_info", "get_object_info", "get_viewport_screenshot", "execute_blender_code", "describe_node_type", "bpy_api_lookup", "export_scene", "record_trajectory_feedback"],
        ["polyhaven"] = ["get_polyhaven_status", "get_polyhaven_categories", "search_polyhaven_assets", "get_polyhaven_asset_preview", "download_polyhaven_asset", "set_texture"],
        ["sketchfab"] = ["get_sketchfab_status", "search_sketchfab_models", "get_sketchfab_model_preview", "download_sketchfab_model"],
        ["polypizza"] = ["get_polypizza_status", "search_polypizza_models", "download_polypizza_model"],
        ["hyper3d"] = ["get_hyper3d_status", "generate_hyper3d_model_via_text", "generate_hyper3d_model_via_images", "poll_rodin_job_status", "import_generated_asset"],
        ["hunyuan3d"] = ["get_hunyuan3d_status", "generate_hunyuan3d_model", "poll_hunyuan_job_status", "import_generated_asset_hunyuan"],
        ["tripo"] = ["get_tripo_status", "generate_tripo_model", "poll_tripo_job_status", "import_generated_asset_tripo"]
    };

    public static async Task<ToolReply> ReadAsync(JsonElement arguments, IMcpClient client, CancellationToken token)
    {
        var tools = await client.ListToolsAsync(token).ConfigureAwait(false);
        var names = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var missing = Groups.Values.SelectMany(x => x).Where(n => !names.Contains(n)).ToArray();
        bool HasParameters(string name, params string[] parameters) =>
            tools.FirstOrDefault(t => t.Name == name)?.InputSchema?["properties"] is JsonObject properties &&
            parameters.All(p => properties.ContainsKey(p));
        var advancedPolyhaven = HasParameters("search_polyhaven_assets", "query", "category", "attributes", "min_size_m", "limit");
        var hunyuanQuality = HasParameters("generate_hunyuan3d_model", "quality");
        JsonObject? addon = null;
        string addonState = "not_probed";
        if (arguments.TryGetProperty("probeAddon", out var probe) && probe.GetBoolean())
        {
            if (!names.Contains("get_addon_status")) addonState = "status_tool_missing";
            else
            {
                var result = await client.CallToolAsync("get_addon_status", new JsonObject { ["user_prompt"] = "" }, token).ConfigureAwait(false);
                addon = BlenderMcpResult.ParseObjectPrefix(BlenderMcpResult.PlainText(result));
                addonState = BlenderMcpResult.IsError("get_addon_status", result) || addon is null ? "unavailable" :
                    addon["source"]?.ToString() != "native" ? "unverified" :
                    addon["up_to_date"] is JsonValue current && current.TryGetValue<bool>(out var upToDate) && !upToDate ? "upgrade_required" :
                    addon["protocol_version"] is not JsonValue protocol || !protocol.TryGetValue<int>(out var version) ? "unverified" :
                    version < 11 ? "upgrade_required" :
                    addon["up_to_date"] is JsonValue flag && flag.TryGetValue<bool>(out var compatible) && compatible ? "compatible" : "unverified";
            }
        }
        return new ToolReply(JsonSerializer.Serialize(new
        {
            baseline = new { version = UpstreamVersion, commit = UpstreamCommit, addonProtocol = 11 },
            catalogTotal = tools.Count, expectedToolCount = Groups.Values.Sum(x => x.Length),
            catalogParity = missing.Length == 0, missingTools = missing,
            schemaFeatures = new { advancedPolyhaven, hunyuanQuality },
            groups = Groups.Select(g => new { name = g.Key, registered = g.Value.Where(names.Contains).ToArray(), missing = g.Value.Where(n => !names.Contains(n)).ToArray() }),
            addonState,
            addonProtocol = addon?["protocol_version"], blenderVersion = addon?["blender_version"],
            telemetryConsent = addon?["telemetry_consent"], premiumGenerators = addon?["premium_generators"],
            providerReadiness = "not_tested; discovery does not verify credentials, integration toggles or quota",
            notes = new[] { "Upgrade server and addon together. Never reload a shared scene without coordination.",
                "Poly Haven 2.1 replaces categories with category; rediscover the exact schema instead of silently dropping filters.",
                "Tripo is Premium-only upstream. No paid generation was requested by this audit.",
                "Safe Mode remains enabled; failures are not automatically replayed." }
        }, WireJson.Options), false);
    }
}
