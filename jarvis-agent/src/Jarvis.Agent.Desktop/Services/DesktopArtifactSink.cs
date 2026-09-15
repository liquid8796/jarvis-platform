using System.IO;
using System.Windows;
using System.Windows.Controls;
using Jarvis.Agent.Core;
using Jarvis.Agent.Windows;
using Jarvis.Protocol;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
namespace Jarvis.Agent.Desktop.Services;
public sealed class DesktopArtifactSink(Window owner) : IArtifactSink
{
    private readonly Queue<Window> _windows = new();
    public Task ShowAsync(WidgetArtifact artifact, CancellationToken cancellationToken) =>
        owner.Dispatcher.InvokeAsync(() => ShowCoreAsync(artifact, cancellationToken)).Task.Unwrap();
    private async Task ShowCoreAsync(WidgetArtifact artifact, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        while (_windows.Count >= 3) { var old = _windows.Dequeue(); if (old.IsLoaded) old.Close(); }
        var view = new WebView2();
        var window = new Window { Title = artifact.Title + " · Jarvis visual", Width = 1020, Height = 740, Content = view,
            WindowStartupLocation = WindowStartupLocation.CenterScreen };
        _windows.Enqueue(window); window.Closed += (_, _) => view.Dispose(); window.Show();
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(AgentProfile.Root, "webview"));
            await view.EnsureCoreWebView2Async(environment); ct.ThrowIfCancellationRequested();
            var core = view.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false; core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.NavigationStarting += (_, args) => { if (args.Uri != "about:blank") args.Cancel = true; };
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) =>
            {
                if (args.Request.Uri.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || args.Request.Uri.StartsWith("https:", StringComparison.OrdinalIgnoreCase) || args.Request.Uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                    args.Response = environment.CreateWebResourceResponse(new MemoryStream(), 403, "Network disabled for widgets", "Content-Type: text/plain");
            };
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            core.NavigationCompleted += (_, args) => { if (args.IsSuccess) loaded.TrySetResult(); else loaded.TrySetException(new IOException("Widget navigation failed.")); };
            core.NavigateToString(SandboxHtml.Wrap(artifact));
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        }
        catch
        {
            if (window.IsLoaded) window.Close();
            throw;
        }
    }
}
