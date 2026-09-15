using System.Security.Principal;
using System.Threading;
using System.Windows;
namespace Jarvis.Agent.Desktop;
public partial class App : Application
{
    private Mutex? _instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new Mutex(true, @"Local\JarvisAgent-" + WindowsIdentity.GetCurrent().User!.Value, out var created);
        if (!created) { MessageBox.Show("Jarvis Agent is already running. Open it from the system tray, or stop the CLI first.", "Jarvis Agent"); Shutdown(); return; }
        var window = new MainWindow(); MainWindow = window; window.Show();
    }
    protected override void OnExit(ExitEventArgs e) { _instance?.Dispose(); base.OnExit(e); }
}
