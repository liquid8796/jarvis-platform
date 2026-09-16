using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Jarvis.Agent.Core;
using Jarvis.Agent.Windows;
using Jarvis.Protocol;
using JarvisCode.Core.Tools.BuiltIn;
namespace Jarvis.Agent.Desktop.Services;
public sealed class LocalPrompts(Window owner, ToolPermissionPolicy permissions, ToolPermissionStore permissionStore) : IApprovalService, IUserQuestions
{
    private readonly SemaphoreSlim _serial = new(1, 1);
    public async Task<bool> ApproveAsync(ToolDescriptor tool, JsonElement arguments, CancellationToken ct)
    {
        await _serial.WaitAsync(ct);
        try
        {
            return await owner.Dispatcher.InvokeAsync(() =>
            {
                ct.ThrowIfCancellationRequested();
                var window = Create("Approve one action", out var panel, out var footer);
                panel.Children.Add(new TextBlock { Text = tool.Name, FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,10) });
                panel.Children.Add(new TextBlock { Text = "An authorized remote client requested this action. Review the exact arguments. Deny anything you did not intend.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,15), Foreground = Brushes.LightGray });
                if (ToolPermissionPolicy.SupportsPermanentApproval(tool.Id))
                    panel.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(46, 38, 25)), BorderBrush = new SolidColorBrush(Color.FromRgb(112, 82, 38)), BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(8), Padding = new Thickness(10), Margin = new Thickness(0,0,0,15),
                        Child = new TextBlock { Text = "Always approve permanently allows future requests for this tool on this device without another Jarvis permission prompt. You can revoke it later in Tool permissions.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Wheat }
                    });
                panel.Children.Add(new TextBox { Text = JsonSerializer.Serialize(arguments, new JsonSerializerOptions { WriteIndented = true }),
                    IsReadOnly = true, Height = 260, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    FontFamily = new FontFamily("Consolas"), FontSize = 12 });
                var deny = new Button { Content = "Deny", IsCancel = true, IsDefault = true, Margin = new Thickness(0,0,10,0) };
                var allow = new Button { Content = "Approve once", Style = (Style)owner.FindResource("Primary") };
                deny.Click += (_, _) => window.DialogResult = false;
                allow.Click += (_, _) => window.DialogResult = true;
                footer.Children.Add(deny);
                if (ToolPermissionPolicy.SupportsPermanentApproval(tool.Id))
                {
                    allow.Margin = new Thickness(0,0,10,0);
                    var always = new Button
                    {
                        Content = "Always approve",
                        ToolTip = "Permanently approve this exact tool on this device until revoked in Tool permissions."
                    };
                    always.Click += (_, _) =>
                    {
                        try
                        {
                            var next = permissions.AlwaysApprovedConstrainedTools.Append(tool.Id).Distinct(StringComparer.Ordinal).ToArray();
                            permissionStore.Save(new ToolPermissionSettings(permissions.FullPermissionTools, next));
                            permissions.GrantAlwaysApprovedConstrainedTool(tool.Id);
                            window.DialogResult = true;
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(window, "Could not save permanent approval. The action was not approved.\n\n" + ex.Message,
                                "Jarvis Agent", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    };
                    footer.Children.Add(allow);
                    footer.Children.Add(always);
                }
                else footer.Children.Add(allow);
                using var registration = ct.Register(() => owner.Dispatcher.BeginInvoke(() => { if (window.IsLoaded) window.Close(); }));
                return window.ShowDialog() == true && !ct.IsCancellationRequested;
            });
        }
        finally { _serial.Release(); }
    }
    public async Task<UserQuestionAnswers?> AskAsync(IReadOnlyList<UserQuestion> questions, CancellationToken ct)
    {
        await _serial.WaitAsync(ct);
        try
        {
            return await owner.Dispatcher.InvokeAsync(() =>
            {
                ct.ThrowIfCancellationRequested();
                var window = Create("Jarvis needs your input", out var panel, out var footer);
                var entries = new List<(UserQuestion Question, List<CheckBox> Checks, TextBox Other)>();
                foreach (var question in questions)
                {
                    panel.Children.Add(new TextBlock { Text = question.Question, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,15,0,7) });
                    var checks = new List<CheckBox>();
                    foreach (var option in question.Options)
                    {
                        var box = new CheckBox { Content = new TextBlock { Text = option.Label + " — " + option.Description, TextWrapping = TextWrapping.Wrap }, Tag = option.Label };
                        box.Checked += (_, _) => { if (!question.MultiSelect) foreach (var other in checks.Where(c => c != box)) other.IsChecked = false; };
                        checks.Add(box); panel.Children.Add(box);
                    }
                    var otherText = new TextBox { Margin = new Thickness(0,10,0,10), ToolTip = "Optional custom answer" };
                    panel.Children.Add(otherText); entries.Add((question, checks, otherText));
                }
                var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0,0,10,0) };
                var submit = new Button { Content = "Submit answers", Style = (Style)owner.FindResource("Primary") };
                submit.Click += (_, _) => window.DialogResult = true; footer.Children.Add(cancel); footer.Children.Add(submit);
                using var registration = ct.Register(() => owner.Dispatcher.BeginInvoke(() => { if (window.IsLoaded) window.Close(); }));
                if (window.ShowDialog() != true || ct.IsCancellationRequested) return null;
                return new UserQuestionAnswers(entries.ToDictionary(e => e.Question.Question,
                    e => string.IsNullOrWhiteSpace(e.Other.Text) ? string.Join(", ", e.Checks.Where(c => c.IsChecked == true).Select(c => (string)c.Tag)) : e.Other.Text));
            });
        }
        finally { _serial.Release(); }
    }
    private Window Create(string title, out StackPanel panel, out StackPanel footer)
    {
        var window = new Window { Title = title + " · Jarvis Agent", Icon = owner.Icon, Width = 640, Height = 580, MinWidth = 480, MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = true };
        if (owner.IsVisible) { window.Owner = owner; window.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        var root = new DockPanel { Margin = new Thickness(26) };
        footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,20,0,0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        panel = new StackPanel(); root.Children.Add(new ScrollViewer { Content = panel }); window.Content = root;
        return window;
    }
}
