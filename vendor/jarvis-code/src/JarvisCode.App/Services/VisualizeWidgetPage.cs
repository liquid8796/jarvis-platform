using System.Text;

namespace JarvisCode.App.Services;

/// <summary>
/// The two documents the widget row loads, and the security header it loads
/// them under.
///
/// The reference does not put the widget runtime in front of the user directly:
/// its <c>AppRenderer</c> (ion-dist <c>cd9b7fccf-BIkz8mbE.js</c>) opens a
/// *sandbox proxy* page on a separate origin, hands it the resource's HTML with
/// <c>ui/notifications/sandbox-resource-ready</c>, and the proxy loads that HTML
/// into an inner iframe under the CSP the resource's <c>_meta.ui</c> declared.
/// The runtime page talks to <c>window.parent</c>, so it needs a parent.
///
/// This port keeps that shape with the parts a WPF host can have: the engine
/// loads <see cref="WrapperHtml"/>, which is the proxy, and the runtime goes
/// into its sandboxed iframe with <see cref="ContentSecurityPolicy"/> injected
/// as a meta tag. The proxy is its own engine session with its own window, and
/// the sandboxed child cannot reach the host channel through it because it is
/// not granted <c>allow-same-origin</c>.
/// </summary>
public static class VisualizeWidgetPage
{
    /// <summary>
    /// The reference appends its own font origin to whatever the resource
    /// declared (<c>[...n?.csp?.resourceDomains ?? [], "https://assets.claude.ai"]</c>),
    /// because the runtime's @font-face rules point there.
    /// </summary>
    public const string FontOrigin = "https://assets.claude.ai";

    /// <summary>
    /// The CSP the widget document runs under. connectDomains map to
    /// <c>connect-src</c> and resourceDomains to <c>img-src</c>,
    /// <c>script-src</c>, <c>style-src</c>, <c>font-src</c> and
    /// <c>media-src</c> — the mapping the reference's MCP Apps schema states in
    /// its own field descriptions (<c>c131c9a32-CQgiruhm.js</c>) — with
    /// <c>frame-src 'none'</c> and <c>base-uri 'self'</c>, which it says are
    /// what an omitted list means.
    ///
    /// The three keywords beyond that list are this build's, and each is
    /// required by the reference's own runtime rather than chosen: the page is
    /// one inline module script over inline styles, and a widget may carry
    /// inline scripts of its own, so <c>'unsafe-inline'</c> and
    /// <c>'unsafe-eval'</c> are what let the recorded page run at all; the
    /// <c>data:</c> and <c>blob:</c> sources are what its own PNG download and
    /// its inline SVG images need.
    /// </summary>
    public static string ContentSecurityPolicy(VisualizeRuntimeSecurity security)
    {
        var resources = string.Join(' ', security.ResourceDomains.Append(FontOrigin));
        var connect = string.Join(' ', security.ConnectDomains);
        return string.Join("; ",
            "default-src 'none'",
            $"script-src 'unsafe-inline' 'unsafe-eval' blob: {resources}",
            $"style-src 'unsafe-inline' {resources}",
            $"img-src data: blob: {resources}",
            $"font-src data: {resources}",
            $"media-src data: blob: {resources}",
            $"connect-src {connect}",
            "frame-src 'none'",
            "base-uri 'self'",
            "form-action 'none'");
    }

    /// <summary>
    /// The runtime with its security header, ready to be handed to the wrapper.
    /// The meta tag goes immediately after <c>&lt;head&gt;</c> so it governs
    /// every style and script the document declares after it.
    /// </summary>
    public static string WithSecurityPolicy(string runtimeHtml, VisualizeRuntimeSecurity security)
    {
        var meta = "<meta http-equiv=\"Content-Security-Policy\" content=\"" +
            ContentSecurityPolicy(security).Replace("\"", "&quot;", StringComparison.Ordinal) + "\">";
        var head = runtimeHtml.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
        return head < 0
            ? meta + runtimeHtml
            : runtimeHtml[..(head + "<head>".Length)] + "\n" + meta + runtimeHtml[(head + "<head>".Length)..];
    }

    /// <summary>
    /// The proxy page. It owns the iframe, relays every message between the
    /// child and the WPF host as a JSON string, and applies the child's own
    /// reported height so the document never scrolls inside itself — the
    /// transcript scrolls instead.
    /// </summary>
    public static string WrapperHtml(
        bool clipboardWrite, string postBinding = DefaultPostBinding, string background = "transparent")
    {
        var allow = clipboardWrite ? " allow=\"clipboard-write\"" : "";
        var builder = new StringBuilder();
        builder.Append("<!doctype html><html><head><meta charset=\"utf-8\">");
        // The proxy is opaque in the card's own colour rather than transparent:
        // an engine window paints its own background where the page does not,
        // and that background is black.
        builder.Append("<style>html,body{margin:0;padding:0;background:");
        builder.Append(background);
        builder.Append(";overflow:hidden}");
        builder.Append("iframe{display:block;width:100%;height:0;border:0;background:transparent}</style>");
        builder.Append("</head><body>");
        builder.Append("<iframe id=\"app\" sandbox=\"allow-scripts allow-popups allow-forms allow-modals\"");
        builder.Append(allow);
        builder.Append("></iframe><script>");
        builder.Append(WrapperBootstrapScript(postBinding));
        builder.Append("</script></body></html>");
        return builder.ToString();
    }

    /// <summary>
    /// The binding the page posts through. The engine has no
    /// <c>chrome.webview</c>: a page answers the host through a DevTools binding
    /// added before it loads, which arrives as a <c>Runtime.bindingCalled</c>
    /// event carrying whatever string the page handed it.
    /// </summary>
    public const string DefaultPostBinding = "__jarvisWidgetPost";

    /// <summary>
    /// The one function the proxy exposes for the host to call. Everything the
    /// host sends is one <c>Runtime.evaluate</c> of it with a JSON string.
    /// </summary>
    public const string HostFunction = "window.__jarvisWidgetHost";

    public static string WrapperBootstrapScript(string postBinding = DefaultPostBinding) =>
        WrapperScript.Replace(DefaultPostBinding, postBinding, StringComparison.Ordinal);

    /// <summary>
    /// The proxy's whole behaviour. Nothing here interprets the protocol: the
    /// host answers every request, and the only message the wrapper acts on
    /// itself is the child's size, which has to reach the iframe element.
    /// </summary>
    private const string WrapperScript = """
        (function () {
          var frame = document.getElementById('app');
          var post = window.__jarvisWidgetPost;
          if (typeof post !== 'function') { return; }
          window.addEventListener('message', async function (event) {
            if (event.source !== frame.contentWindow) { return; }
            var data = event.data;
            if (data && (data.type === 'anthropic:attach-files' || data.type === 'anthropic:elicit-submit' || data.type === 'anthropic:connect-connector')) {
              // The activation is sampled in this host world, never trusted
              // from widget JSON, and sampled before asynchronous file reads.
              if (!navigator.userActivation.isActive) { return; }
              var forwarded = { type: data.type, __userActivated: true };
              if (data.type === 'anthropic:connect-connector') {
                if (typeof data.connectorId !== 'string') { return; }
                forwarded.connectorId = data.connectorId;
              } else {
                if (!Array.isArray(data.files)) { return; }
                var files = data.files.filter(function (file) { return file instanceof File; });
                if (!files.length || files.length > 20 || files.some(function (file) { return file.size > 30 * 1024 * 1024; }) || files.reduce(function (sum, file) { return sum + file.size; }, 0) > 60 * 1024 * 1024) { return; }
                forwarded.files = [];
                for (var file of files) {
                  var bytes = new Uint8Array(await file.arrayBuffer()), binary = '';
                  for (var offset = 0; offset < bytes.length; offset += 32768) binary += String.fromCharCode.apply(null, bytes.subarray(offset, offset + 32768));
                  forwarded.files.push({ name: file.name, mimeType: file.type, data: btoa(binary) });
                }
                if (data.type === 'anthropic:elicit-submit') {
                  if (typeof data.text !== 'string') { return; }
                  forwarded.text = data.text;
                }
              }
              post(JSON.stringify(forwarded));
              return;
            }
            if (data && data.method === 'ui/notifications/size-changed' && data.params) {
              var height = data.params.height;
              if (typeof height === 'number' && height > 0) { frame.style.height = height + 'px'; }
            }
            try { post(JSON.stringify(data)); } catch (error) { /* not serializable */ }
          });
          window.__jarvisWidgetHost = function (json) {
            var message = JSON.parse(json);
            if (message.__host === 'resource') { frame.srcdoc = message.html; return; }
            if (frame.contentWindow) { frame.contentWindow.postMessage(message, '*'); }
          };
          post(JSON.stringify({ __host: 'ready' }));
        })();
        """;
}
