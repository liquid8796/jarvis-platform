using System.Net;
namespace Jarvis.Protocol;
/// <summary>No network, native bridge, file access, forms, popups or same-origin privileges for generated widgets.</summary>
public static class SandboxHtml
{
    public static string Wrap(WidgetArtifact artifact)
    {
        var title = WebUtility.HtmlEncode(artifact.Title);
        var body = "<meta charset='utf-8'><style>:root{color-scheme:dark;--color-text-primary:#edeef5;--color-text-secondary:#a1a8ba;--color-background-primary:#151923;--color-background-secondary:#1e2332;--font-sans:Segoe UI,system-ui,sans-serif}body{margin:20px;font-family:var(--font-sans);color:var(--color-text-primary);background:var(--color-background-primary)}*{box-sizing:border-box}</style>" + artifact.Html;
        return "<!doctype html><html><head><meta charset='utf-8'><meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data: blob:; font-src data:; frame-src 'self'; connect-src 'none'; base-uri 'none'; form-action 'none'\"><meta name='referrer' content='no-referrer'><title>" + title +
            "</title><style>html,body{margin:0;height:100%;background:#151923}iframe{border:0;width:100%;height:100%}</style></head><body><iframe title='" + title +
            "' sandbox='allow-scripts' referrerpolicy='no-referrer' srcdoc=\"" + WebUtility.HtmlEncode(body) + "\"></iframe></body></html>";
    }
}
