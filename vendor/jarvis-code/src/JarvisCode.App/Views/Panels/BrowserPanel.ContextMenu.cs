using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views.Panels;

/// <summary>
/// The page context menu's own rows, ported from the reference's web-contents menu
/// (app.asar <c>index.chunk-DnlgCaT3.js</c>, its <c>zV</c>): a link offers to open
/// in the default browser and to copy its address, and an image offers to copy the
/// image and its address.
///
/// The engine has no context menu of its own — Electron reports the click and
/// leaves the menu to its host — so this builds the whole menu rather than
/// inserting rows into the browser's, and carries the edit rows the reference's
/// menu also shows.
///
/// One of that menu's sections is deliberately absent here and declared in the
/// parity suite's surface manifest: the spelling suggestions and "Add to
/// dictionary", which the engine reports no misspelled word for — the composer
/// carries those instead, against WPF's own checker.
/// </summary>
public partial class BrowserPanel
{
    public const string OpenLinkInDefaultBrowserLabel = "Open Link in Default Browser";
    public const string CopyLinkAddressLabel = "Copy Link Address";
    public const string CopyImageLabel = "Copy Image";
    public const string CopyImageAddressLabel = "Copy Image Address";

    /// <summary>
    /// Builds the menu for one context-menu click. The parameters are Electron's
    /// own (<c>linkURL</c>, <c>srcURL</c>, <c>mediaType</c>, <c>isEditable</c>,
    /// <c>selectionText</c>), which carry what WebView2 reported as a target.
    /// </summary>
    private void OnEngineContextMenu(string engineTabId, JsonObject parameters)
    {
        if (TabForEngine(engineTabId) is not { EngineTabId: { } tabId } || _session is not { } session)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.MousePoint };

        void Add(string label, Action click)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => click();
            menu.Items.Add(item);
        }

        var link = parameters["linkURL"]?.GetValue<string>() ?? "";
        if (link.Length > 0)
        {
            var isWeb = Uri.TryCreate(link, UriKind.Absolute, out var uri) &&
                uri.Scheme is "http" or "https" &&
                string.IsNullOrEmpty(uri.UserInfo);
            if (isWeb)
            {
                Add(OpenLinkInDefaultBrowserLabel, () => ExternalLinks.Open(Window.GetWindow(this), link));
            }

            Add(CopyLinkAddressLabel, () => CopyText(link));
        }

        var source = parameters["srcURL"]?.GetValue<string>() ?? "";
        if (parameters["mediaType"]?.GetValue<string>() == "image")
        {
            Add(CopyImageLabel, () => _ = CopyImageAsync(session.Cdp(tabId), source));
            if (source.Length > 0)
            {
                Add(CopyImageAddressLabel, () => CopyText(source));
            }
        }

        // The engine draws no menu of its own, so the ordinary edit rows are this
        // menu's to provide; the reference's shows them too.
        var selection = parameters["selectionText"]?.GetValue<string>() ?? "";
        var editable = parameters["isEditable"]?.GetValue<bool>() == true;
        if (selection.Length > 0)
        {
            Add("Copy", () => CopyText(selection));
        }

        if (editable)
        {
            Add("Paste", () => _ = PasteIntoPageAsync(session.Cdp(tabId)));
            Add("Select All", () => _ = BrowserPaneCdp.EvaluateAsync(
                session.Cdp(tabId), "document.execCommand('selectAll')", replMode: false));
        }

        if (menu.Items.Count == 0)
        {
            return;
        }

        menu.IsOpen = true;
    }

    /// <summary>The clipboard's text, typed into whatever the page has focused.</summary>
    private static async Task PasteIntoPageAsync(IPaneCdp core)
    {
        string text;
        try
        {
            text = Clipboard.GetText();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return;
        }

        if (text.Length == 0)
        {
            return;
        }

        try
        {
            await BrowserPaneCdp.CallAsync(core, "Input.insertText", new JsonObject { ["text"] = text });
        }
        catch (InvalidOperationException)
        {
            // The tab went away; nothing was typed.
        }
    }

    private static void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process owns the clipboard.
        }
    }

    /// <summary>
    /// Copies the image itself, read out of the page rather than off a URL —
    /// which is what keeps a data: or blob: image copyable.
    /// </summary>
    private static async Task CopyImageAsync(IPaneCdp core, string source)
    {
        if (source.Length == 0)
        {
            return;
        }

        var script = $$"""
            (async () => {
              const response = await fetch({{System.Text.Json.JsonSerializer.Serialize(source)}});
              const blob = await response.blob();
              const buffer = await blob.arrayBuffer();
              const bytes = new Uint8Array(buffer);
              let binary = '';
              for (let i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i]);
              return btoa(binary);
            })()
            """;

        try
        {
            if (await BrowserPaneCdp.EvaluateAsync(core, script, replMode: false) is not { } node ||
                node.GetValue<string>() is not { Length: > 0 } base64)
            {
                return;
            }

            var bytes = Convert.FromBase64String(base64);
            using var stream = new System.IO.MemoryStream(bytes);
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            Clipboard.SetImage(decoder.Frames[0]);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException
            or NotSupportedException or ArgumentException or InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            // The image could not be read back; nothing lands on the clipboard.
        }
    }
}
