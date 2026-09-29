using System.IO;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.Core.Tools;

namespace Jarvis.Agent.Windows.Tests;

public sealed class BrowserFullPermissionTests
{
    [Fact]
    public async Task Host_full_permission_bypasses_origin_consent_without_mutating_saved_allowed_sites()
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-browser-consent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new UiSettingsStore(Path.Combine(root, "computer-settings.json"));
            var gate = new BrowserOriginGate(null!, store);
            var asked = false;
            var context = new ToolExecutionContext
            {
                WorkingDirectory = root,
                BypassBrowserOriginConsent = true,
                AskUserAsync = (_, _) =>
                {
                    asked = true;
                    throw new InvalidOperationException("Origin prompt should have been bypassed.");
                }
            };
            var refusal = await gate.CheckAsync("navigate",
                new JsonObject { ["url"] = "https://example.com/private" }, context, CancellationToken.None);
            Assert.Null(refusal);
            Assert.False(asked);
            Assert.Empty(store.Current.BrowserAllowedOrigins);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
