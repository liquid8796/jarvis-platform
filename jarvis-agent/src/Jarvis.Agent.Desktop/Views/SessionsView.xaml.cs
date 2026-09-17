using System.Windows.Controls;
using Jarvis.Agent.Desktop.Infrastructure;

namespace Jarvis.Agent.Desktop.Views;

public partial class SessionsView : UserControl
{
    public SessionsView()
    {
        SessionPalette.EnsureInitialized();
        InitializeComponent();
    }
}
