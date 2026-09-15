using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json.Nodes;

namespace JarvisCode.App.Services;

/// <summary>
/// Key-combo parsing for the Browser pane's computer{action:"key"} — the
/// reference desktop's AOn/jOn (app.asar): alt=1, ctrl=2, meta/cmd/win=4,
/// shift=8; the key is dispatched by its DOM key name only (no virtual-key
/// mapping), and single characters with no modifiers also insert their text.
/// </summary>
public static class BrowserPaneKeys
{
    public static int Modifiers(string combo)
    {
        var bits = 0;
        foreach (var part in combo.ToLowerInvariant().Split('+'))
        {
            bits |= part switch
            {
                "alt" => 1,
                "ctrl" or "control" => 2,
                "meta" or "cmd" or "win" or "windows" => 4,
                "shift" => 8,
                _ => 0,
            };
        }

        return bits;
    }

    /// <summary>Parses one token ("ctrl+a", "Enter", "ctrl++"); null when empty.</summary>
    public static (string Key, int ModifierBits, string? Text)? ParseToken(string token)
    {
        var parts = token.Split('+').ToList();
        var key = parts[^1];
        parts.RemoveAt(parts.Count - 1);
        if (key.Length == 0 && parts.Count > 0)
        {
            // A trailing "+" means the plus key itself ("ctrl++").
            parts.RemoveAt(parts.Count - 1);
            key = "+";
        }

        if (key.Length == 0)
        {
            return null;
        }

        var modifiers = Modifiers(string.Join('+', parts));
        var text = key.Length == 1 && modifiers == 0 ? key : null;
        return (key, modifiers, text);
    }
}

/// <summary>
/// One DevTools-protocol endpoint: a pane tab that commands can be sent to.
/// The pane's engine implements it, so the call shapes below are written once
/// and do not name the browser underneath them.
/// </summary>
public interface IPaneCdp
{
    Task<JsonObject> SendAsync(
        string method,
        JsonObject? parameters,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The DevTools-protocol half of driving the Browser pane — the
/// counterpart of the reference desktop's CDPTools (app.asar). Input events, JPEG
/// screenshots, REPL evaluation and viewport emulation all follow the
/// reference's exact CDP call shapes. UI thread only.
/// </summary>
public static class BrowserPaneCdp
{
    private const string MobileUserAgent =
        "Mozilla/5.0 (Linux; Android 12; Pixel 6) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/126.0.0.0 Mobile Safari/537.36";

    public static Task<JsonObject> CallAsync(
        IPaneCdp core,
        string method,
        JsonObject? parameters = null,
        CancellationToken cancellationToken = default) =>
        core.SendAsync(method, parameters, cancellationToken);

    /// <summary>Turns on the CDP domains the capture buffers listen to.</summary>
    public static async Task EnableCaptureAsync(IPaneCdp core)
    {
        foreach (var domain in new[] { "Runtime.enable", "Log.enable", "Network.enable", "Page.enable" })
        {
            await core.SendAsync(domain, null);
        }
    }

    /// <summary>
    /// The reference CDPTools.evaluate: awaitPromise, 30s timeout; replMode
    /// serializes through callFunctionOn so values that resist returnByValue
    /// still come back. Exceptions rethrow with the page's own description.
    /// </summary>
    public static async Task<JsonNode?> EvaluateAsync(
        IPaneCdp core,
        string expression,
        bool replMode,
        CancellationToken cancellationToken = default)
    {
        var parameters = new JsonObject
        {
            ["expression"] = expression,
            ["returnByValue"] = !replMode,
            ["awaitPromise"] = true,
            ["timeout"] = 30000,
        };
        if (replMode)
        {
            parameters["replMode"] = true;
        }

        var result = await CallAsync(core, "Runtime.evaluate", parameters, cancellationToken);
        ThrowOnException(result);
        var value = result["result"] as JsonObject ?? [];
        if (value["objectId"]?.GetValue<string>() is { } objectId)
        {
            var serialized = await CallAsync(core, "Runtime.callFunctionOn", new JsonObject
            {
                ["objectId"] = objectId,
                ["functionDeclaration"] = "function(){return this}",
                ["awaitPromise"] = true,
                ["returnByValue"] = true,
            }, cancellationToken);
            ThrowOnException(serialized);
            value = serialized["result"] as JsonObject ?? [];
        }

        return value["value"]?.DeepClone();
    }

    private static void ThrowOnException(JsonObject result)
    {
        if (result["exceptionDetails"] is not JsonObject details)
        {
            return;
        }

        var exception = details["exception"] as JsonObject;
        throw new InvalidOperationException(
            exception?["description"]?.GetValue<string>()
            ?? exception?["value"]?.ToString()
            ?? details["text"]?.GetValue<string>() ?? "Evaluation failed");
    }

    /// <summary>
    /// JPEG screenshot. scale &lt; 1 downsizes the returned image while the
    /// frame (coordinate space) stays full resolution, like the reference:
    /// coordinates are ALWAYS in the full-resolution frame.
    /// </summary>
    public static async Task<PaneScreenshot> ScreenshotJpegAsync(IPaneCdp core, double scale)
    {
        var shot = await CallAsync(core, "Page.captureScreenshot", new JsonObject
        {
            ["format"] = "jpeg",
            ["quality"] = 80,
        });
        var data = Convert.FromBase64String(shot["data"]?.GetValue<string>() ?? "");
        using var stream = new MemoryStream(data);
        using var image = System.Drawing.Image.FromStream(stream);
        var frameWidth = image.Width;
        var frameHeight = image.Height;
        if (scale >= 1)
        {
            return new PaneScreenshot(Convert.ToBase64String(data), frameWidth, frameHeight, frameWidth, frameHeight);
        }

        var width = Math.Max(1, (int)Math.Round(frameWidth * scale));
        var height = Math.Max(1, (int)Math.Round(frameHeight * scale));
        using var resized = new Bitmap(image, width, height);
        using var output = new MemoryStream();
        resized.Save(output, ImageFormat.Jpeg);
        return new PaneScreenshot(Convert.ToBase64String(output.ToArray()), width, height, frameWidth, frameHeight);
    }

    /// <summary>Press/release pairs with rising clickCount — the reference's dispatchMouseAt.</summary>
    public static async Task MouseClickAsync(
        IPaneCdp core, double x, double y, string button, int clickCount, int modifiers,
        CancellationToken cancellationToken = default)
    {
        for (var i = 1; i <= clickCount; i++)
        {
            Exception? pressError = null;
            try
            {
                await CallAsync(core, "Input.dispatchMouseEvent", new JsonObject
                {
                    ["type"] = "mousePressed", ["x"] = x, ["y"] = y, ["button"] = button,
                    ["clickCount"] = i, ["modifiers"] = modifiers,
                }, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception ex)
            {
                pressError = ex;
                throw;
            }
            finally
            {
                // Once a down packet was attempted, cancellation must not suppress its matching
                // release: the engine may execute a request after its local waiter was cancelled.
                using var releaseDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await CallAsync(core, "Input.dispatchMouseEvent", new JsonObject
                    {
                        ["type"] = "mouseReleased", ["x"] = x, ["y"] = y, ["button"] = button,
                        ["clickCount"] = i, ["modifiers"] = modifiers,
                    }, releaseDeadline.Token);
                }
                catch (Exception) when (pressError is not null)
                {
                    // Preserve the original cancellation/failure; the bounded release was best effort.
                }
            }
        }
    }

    /// <summary>press → mid-move → end-move → release, buttons held — the reference's dragFromTo.</summary>
    public static async Task DragAsync(IPaneCdp core, double startX, double startY, double endX, double endY)
    {
        startX = Math.Round(startX);
        startY = Math.Round(startY);
        endX = Math.Round(endX);
        endY = Math.Round(endY);
        await CallAsync(core, "Input.dispatchMouseEvent", new JsonObject
        {
            ["type"] = "mousePressed", ["x"] = startX, ["y"] = startY,
            ["button"] = "left", ["buttons"] = 1, ["clickCount"] = 1,
        });
        await CallAsync(core, "Input.dispatchMouseEvent", new JsonObject
        {
            ["type"] = "mouseMoved", ["x"] = (startX + endX) / 2, ["y"] = (startY + endY) / 2,
            ["button"] = "left", ["buttons"] = 1,
        });
        await CallAsync(core, "Input.dispatchMouseEvent", new JsonObject
        {
            ["type"] = "mouseMoved", ["x"] = endX, ["y"] = endY,
            ["button"] = "left", ["buttons"] = 1,
        });
        await CallAsync(core, "Input.dispatchMouseEvent", new JsonObject
        {
            ["type"] = "mouseReleased", ["x"] = endX, ["y"] = endY,
            ["button"] = "left", ["clickCount"] = 1,
        });
    }

    /// <summary>
    /// keyDown/keyUp by DOM key name — the reference dispatches {key, modifiers,
    /// text?} on both events and never maps virtual-key codes.
    /// </summary>
    public static async Task PressKeyAsync(
        IPaneCdp core,
        string key,
        int modifiers,
        string? text,
        CancellationToken cancellationToken = default)
    {
        var fields = new JsonObject { ["key"] = key, ["modifiers"] = modifiers };
        if (text is not null)
        {
            fields["text"] = text;
        }

        Exception? downError = null;
        try
        {
            var down = (JsonObject)fields.DeepClone();
            down["type"] = "keyDown";
            await CallAsync(core, "Input.dispatchKeyEvent", down, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception ex)
        {
            downError = ex;
            throw;
        }
        finally
        {
            // Never leave a key logically held when cancellation races the down response.
            using var releaseDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var up = (JsonObject)fields.DeepClone();
                up["type"] = "keyUp";
                await CallAsync(core, "Input.dispatchKeyEvent", up, releaseDeadline.Token);
            }
            catch (Exception) when (downError is not null)
            {
                // Preserve the original cancellation/failure; the bounded release was best effort.
            }
        }
    }

    /// <summary>
    /// The reference CDPTools.setViewport: mobile defaults to width &lt; 768,
    /// mobile devices render at deviceScaleFactor 2 with the Android UA, five
    /// touch points and mouse-to-touch translation. A size wider than the pane
    /// is scaled down to fit.
    /// </summary>
    public static async Task SetViewportAsync(IPaneCdp core, int width, int height, bool? mobile, double? fitScale)
    {
        var isMobile = mobile ?? width < 768;
        var metrics = new JsonObject
        {
            ["width"] = width,
            ["height"] = height,
            ["deviceScaleFactor"] = isMobile ? 2 : 0,
            ["mobile"] = isMobile,
        };
        if (fitScale is { } scale && scale < 1)
        {
            metrics["scale"] = Math.Clamp(scale, 0.2, 1.0);
        }

        await CallAsync(core, "Emulation.setDeviceMetricsOverride", metrics);
        await ApplyMobileOverridesAsync(core, isMobile);
    }

    public static async Task ClearViewportAsync(IPaneCdp core)
    {
        await CallAsync(core, "Emulation.clearDeviceMetricsOverride");
        await ApplyMobileOverridesAsync(core, mobile: false);
    }

    private static async Task ApplyMobileOverridesAsync(IPaneCdp core, bool mobile)
    {
        var agent = new JsonObject { ["userAgent"] = mobile ? MobileUserAgent : "" };
        if (mobile)
        {
            agent["userAgentMetadata"] = new JsonObject
            {
                ["brands"] = new JsonArray(
                    new JsonObject { ["brand"] = "Chromium", ["version"] = "126" },
                    new JsonObject { ["brand"] = "Google Chrome", ["version"] = "126" }),
                ["fullVersion"] = "126.0.0.0",
                ["platform"] = "Android",
                ["platformVersion"] = "12",
                ["architecture"] = "",
                ["model"] = "Pixel 6",
                ["mobile"] = true,
            };
        }

        await CallAsync(core, "Emulation.setUserAgentOverride", agent);
        var touch = new JsonObject { ["enabled"] = mobile };
        if (mobile)
        {
            touch["maxTouchPoints"] = 5;
        }

        await CallAsync(core, "Emulation.setTouchEmulationEnabled", touch);
        await CallAsync(core, "Emulation.setEmitTouchEventsForMouse", new JsonObject
        {
            ["enabled"] = mobile,
        });
    }

    /// <summary>prefers-color-scheme emulation on one tab (survives reloads until re-synced).</summary>
    public static Task SetEmulatedColorSchemeAsync(IPaneCdp core, string scheme) =>
        CallAsync(core, "Emulation.setEmulatedMedia", new JsonObject
        {
            ["features"] = new JsonArray(new JsonObject
            {
                ["name"] = "prefers-color-scheme", ["value"] = scheme,
            }),
        });
}
