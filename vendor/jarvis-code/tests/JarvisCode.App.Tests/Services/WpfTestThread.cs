using System.Windows;
using System.Windows.Threading;

namespace JarvisCode.App.Tests.Services;

[CollectionDefinition("Native window tests", DisableParallelization = true)]
public sealed class NativeWindowTestCollection;

/// <summary>One WPF application per test process; all its windows remain offscreen and are closed after each action.</summary>
internal static class WpfTestThread
{
    private static readonly Lazy<Dispatcher> Ui = new(() =>
    {
        if (!NativeUiTests.Enabled)
            throw new InvalidOperationException(NativeUiTests.DisabledReason + " Mark the calling test with NativeUiFact/NativeUiTheory.");
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                foreach (var source in new[] { "Styles/Base.xaml", "Styles/Icons.xaml", "Views/Panels/PanelChrome.xaml" })
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri("/JarvisCode.App;component/" + source, UriKind.Relative),
                    });
                ready.SetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            }
            catch (Exception error) { ready.TrySetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
    });

    public static void Run(Action action) => Ui.Value.Invoke(() =>
    {
        try { action(); }
        finally { foreach (var window in Application.Current.Windows.OfType<Window>().ToArray()) window.Close(); }
    });
}
