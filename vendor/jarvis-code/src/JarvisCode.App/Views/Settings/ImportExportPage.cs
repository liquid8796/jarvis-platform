using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using JarvisCode.App.Composition;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views.Settings;

/// <summary>
/// Settings › Import &amp; export — the reference's Import page (ion-dist
/// <c>c71860c77-BZhYsZYJ.js</c>): the Import section (<c>9XUYQtJvwF</c>) with its
/// "Import from Claude" wizard entry (<c>3LTMwU3z0y</c> /<c>PMVaogew9J</c>) and
/// "Import…" (<c>MSAzF5WZ27</c>); the Export section (<c>SVwJTM5AmL</c>) with
/// "Export sessions" (<c>djGqYEVUQz</c>), "Saves to your Downloads folder"
/// (<c>tZKbSCxm4Q</c>), "Export sessions from" (<c>PGl4CaxlOx</c>), "Export…"
/// (<c>gcXG4edAhu</c>) and "Show in Explorer" (<c>ySAk9qhogV</c>); and the
/// Import history section (<c>EUNZRB0Gmy</c>) with "No imports yet"
/// (<c>dv4SpB8M8J</c>), "Remove all" (<c>jNai7bvfRf</c>), the two confirmations
/// (<c>Kjopb5UhrC</c> / <c>xVu+C0rCcm</c>) and "Remove" (<c>G/yZLul6P1</c>).
///
/// Two of the reference's three import sources need a claude.ai account — signing
/// in to fetch an export, and reading a <c>data-export-*.zip</c> that account
/// produced — so what this page offers is its third: another install's data
/// folder, plus the Claude Code CLI transcripts this build already converts.
/// </summary>
internal sealed class ImportExportPage(AppServices services)
{
    public const string PageTitle = "Import & export";

    private string _importRange = "30";
    private string _exportRange = "30";
    private StackPanel? _historyHost;
    private TextBlock? _exportCount;
    private TextBlock? _status;

    public FrameworkElement Build()
    {
        var page = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        page.Children.Add(BuildImport());
        page.Children.Add(BuildExport());
        page.Children.Add(BuildHistory());
        return page;
    }

    private FrameworkElement BuildImport()
    {
        var section = SettingsRows.Section("Import");

        _status = SettingsRows.Muted("");
        _status.Visibility = Visibility.Collapsed;

        section.Children.Add(SettingsRows.Row(
            "Import from Jarvis",
            "Bring over your sessions from another install",
            SettingsRows.SecondaryButton("Import…", ImportFromFolder)));

        section.Children.Add(SettingsRows.Row(
            "Import sessions from",
            "Pick how far back to go. Everything from the source in that window is copied over.",
            SettingsRows.Select(
                [.. SessionTransfer.Ranges.Select(r => (r.Value, r.Label, (string?)null))],
                _importRange,
                value => _importRange = value,
                width: 180,
                accessibleName: "Import sessions from")));

        section.Children.Add(SettingsRows.Row(
            "Import Claude Code CLI sessions",
            "Sessions started in the terminal (under ~/.claude/projects) are converted into Code sessions, keeping their conversation history. Already-imported sessions are skipped.",
            SettingsRows.SecondaryButton("Import…", () =>
            {
                if (Application.Current.MainWindow is MainWindow main)
                {
                    _ = main.ImportCliSessionsAsync();
                }
            })));

        section.Children.Add(SettingsRows.Block(SettingsUi.Caption(
            "Sessions are copied, not moved — your original history stays where it is, and re-importing won’t create duplicates.")));
        section.Children.Add(SettingsRows.Block(_status));
        return section;
    }

    private void ImportFromFolder()
    {
        using var picker = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose the app’s data folder (its name starts with “Jarvis”).",
            UseDescriptionForTitle = true,
        };
        if (picker.ShowDialog() != System.Windows.Forms.DialogResult.OK || picker.SelectedPath.Length == 0)
        {
            return;
        }

        var result = SessionTransfer.Import(services.Paths, picker.SelectedPath, SessionTransfer.DaysFor(_importRange));
        Report(result.Error ?? (result.Sessions == 0
            ? "Nothing new to import"
            : $"Imported {result.Sessions} session{(result.Sessions == 1 ? "" : "s")}" +
              (result.Skipped > 0 ? $" · {result.Skipped} already imported and skipped." : ".")));
        RenderHistory();
    }

    private void Report(string message)
    {
        if (_status is null)
        {
            return;
        }

        _status.Text = message;
        _status.Visibility = Visibility.Visible;
    }

    private FrameworkElement BuildExport()
    {
        var section = SettingsRows.Section("Export");

        _exportCount = SettingsRows.Footnote("");
        UpdateExportCount();

        section.Children.Add(SettingsRows.Row(
            "Export sessions",
            "Saves to your Downloads folder",
            SettingsRows.SecondaryButton("Export…", RunExport)));

        section.Children.Add(SettingsRows.Row(
            "Export sessions from",
            null,
            SettingsRows.Select(
                [.. SessionTransfer.Ranges.Select(r => (r.Value, r.Label, (string?)null))],
                _exportRange,
                value =>
                {
                    _exportRange = value;
                    UpdateExportCount();
                },
                width: 180,
                accessibleName: "Export sessions from"),
            below: _exportCount));

        section.Children.Add(SettingsRows.Block(SettingsUi.Caption(
            "From this install only. Terminal sessions aren’t included.")));
        return section;
    }

    private void UpdateExportCount()
    {
        if (_exportCount is null)
        {
            return;
        }

        var (sessions, bytes) = SessionTransfer.Measure(services.Paths, SessionTransfer.DaysFor(_exportRange));
        _exportCount.Text = sessions == 0
            ? "No sessions in this range"
            : $"{sessions} session{(sessions == 1 ? "" : "s")} · {SessionTransfer.Size(bytes)}";
    }

    private void RunExport()
    {
        var result = SessionTransfer.Export(services.Paths, SessionTransfer.DaysFor(_exportRange));
        if (result.Error is not null || result.Path is null)
        {
            Report(result.Error ?? "Couldn’t write the export. Free some disk space or try again.");
            return;
        }

        var path = result.Path;
        var done = MessageBox.Show(
            $"{Path.GetFileName(path)} · {result.Sessions} session{(result.Sessions == 1 ? "" : "s")}",
            "Done",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);
        if (done == MessageBoxResult.OK)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
            {
                // Explorer failing to open is not actionable here.
            }
        }
    }

    private FrameworkElement BuildHistory()
    {
        var removeAll = SettingsRows.SecondaryButton("Remove all", () =>
        {
            if (MessageBox.Show(
                    "Every session these imports brought in will be deleted. This can’t be undone.",
                    "Remove all imports?",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning) != MessageBoxResult.OK)
            {
                return;
            }

            SessionTransfer.RemoveAll(services.Paths);
            RenderHistory();
        });

        var section = SettingsRows.Section("Import history", null, removeAll);
        _historyHost = new StackPanel();
        section.Children.Add(_historyHost);
        RenderHistory();
        return section;
    }

    private void RenderHistory()
    {
        if (_historyHost is null)
        {
            return;
        }

        _historyHost.Children.Clear();
        var history = SessionTransfer.History(services.Paths);
        if (history.Count == 0)
        {
            _historyHost.Children.Add(SettingsRows.Muted("No imports yet"));
            return;
        }

        foreach (var record in history)
        {
            var captured = record;
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            var title = new TextBlock
            {
                Text = record.Source,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = record.Source,
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
            text.Children.Add(title);
            text.Children.Add(SettingsRows.Footnote(
                $"{record.Sessions} session{(record.Sessions == 1 ? "" : "s")} · {SessionTransfer.Ago(record.When)}"));
            row.Children.Add(text);

            var remove = SettingsRows.SecondaryButton("Remove", () =>
            {
                if (MessageBox.Show(
                        $"{captured.Sessions} session{(captured.Sessions == 1 ? "" : "s")} will be deleted. This can’t be undone.",
                        "Remove this import?",
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Warning) != MessageBoxResult.OK)
                {
                    return;
                }

                SessionTransfer.Remove(services.Paths, captured);
                RenderHistory();
            });
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            _historyHost.Children.Add(SettingsUi.Card(row));
        }
    }
}
