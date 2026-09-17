using System.Windows.Controls;
using Jarvis.Agent.Desktop.Infrastructure;

namespace Jarvis.Agent.Desktop.Views;

public partial class ExecutionSettingsView : UserControl
{
    public ExecutionSettingsView()
    {
        SessionPalette.EnsureInitialized();
        InitializeComponent();
    }
}
