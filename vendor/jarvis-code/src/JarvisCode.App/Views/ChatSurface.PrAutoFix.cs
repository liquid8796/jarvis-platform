using System.Windows;
using System.Windows.Controls;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views;

public partial class ChatSurface
{
    private void ShowPrAutomationMenu()
    {
        if (_vm is null || _gitBarInput.Pr is not { } pr) return;
        var menu = new ContextMenu { PlacementTarget = _gitBar, MinWidth = 260 };
        var open = new MenuItem { Header = "View checks on GitHub" };
        open.Click += (_, _) => OpenUrl(pr.Url);
        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        var current = _vm.PrAutoFix;
        AddSwitch("Auto-fix pull requests", current?.AutoFix == true, value => SetPrAutomation(value, null));
        AddSwitch("Address review comments", current?.AutoFix == true, value => SetPrAutomation(value, null));
        AddSwitch("Auto-archive on close", current?.AutoArchive == true, value => SetPrAutomation(null, value));
        menu.IsOpen = true;

        void AddSwitch(string label, bool selected, Action<bool> change)
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = selected,
                IsEnabled = pr.State is not (PrDisplayState.None or PrDisplayState.Merged or PrDisplayState.Closed) };
            item.Click += (_, _) => change(item.IsChecked);
            menu.Items.Add(item);
        }
    }

    private void SetPrAutomation(bool? autoFix, bool? autoArchive)
    {
        if (_vm is null || _gitBarInput.Pr is not { } pr) return;
        var previous = _vm.PrAutoFix;
        try
        {
            _vm.ConfigurePrAutoFix(new(pr.Number, pr.Url, _vm.Session.WorkingDirectory, _gitBarInput.BranchName)
            {
                AutoFix = autoFix ?? previous?.AutoFix ?? false,
                AutoArchive = autoArchive ?? previous?.AutoArchive ?? false,
                BaseBranch = pr.BaseRefName,
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        { _vm.Transcript.Add(new ViewModels.NoticeItem { Text = ex.Message, IsError = true }); }
    }

    private async Task ResumePrAutomationAsync()
    {
        if (!_isCodeSurface || _services is null || _sessions is null) return;
        foreach (var entry in _services.UiSettings.Current.SessionPrAutoFix.ToArray())
        {
            if ((!entry.Value.AutoFix && !entry.Value.AutoArchive) || _services.SessionGroups.IsArchived(entry.Key)) continue;
            var session = await _services.Sessions.LoadAsync(entry.Key);
            if (session is not null) _sessions.ForSession(session);
        }
    }
}
