using System;
using System.IO;
using System.Collections.ObjectModel;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Diagnostics;
using Expression = System.Linq.Expressions.Expression;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal sealed class BindingErrors : TraceListener
{
    public List<string> Messages { get; } = [];
    public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message); }
    public override void WriteLine(string? message) => Write(message);
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var ptyOnly = args.Length == 3 && args[2] == "--pty-only";
        var promptsOnly = args.Length == 3 && args[2] == "--prompts-only";
        if (args.Length != 2 && !ptyOnly && !promptsOnly)
        {
            Console.Error.WriteLine("Usage: UiSmoke <published-agent-directory> <report-directory> [--pty-only|--prompts-only]");
            return 2;
        }
        var publish = Path.GetFullPath(args[0]); var report = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(report);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var file = Path.Combine(publish, name.Name + ".dll");
            return File.Exists(file) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(file) : null;
        };
        Window? window = null;
        try
        {
            if (ptyOnly)
            {
                PublishedPtySmoke.Run(publish, report).GetAwaiter().GetResult();
                Console.WriteLine(File.ReadAllText(Path.Combine(report, "published-pty-smoke.json")));
                return 0;
            }
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(publish, "Jarvis.Agent.Desktop.dll"));
            var appType = assembly.GetType("Jarvis.Agent.Desktop.App", throwOnError: true)!;
            var bindingErrors = new BindingErrors();
            PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            var app = (Application)Activator.CreateInstance(appType)!;
            appType.GetMethod("InitializeComponent")!.Invoke(app, null);
            var windowType = assembly.GetType("Jarvis.Agent.Desktop.MainWindow", throwOnError: true)!;
            var settingsRoot = Path.Combine(report, "isolated-settings");
            window = (Window)Activator.CreateInstance(windowType, [settingsRoot, false])!;
            var model = window.DataContext; var modelType = model.GetType();
            void Set(string property, object value) => modelType.GetProperty(property)!.SetValue(model, value);
            object? Get(string property) => modelType.GetProperty(property)!.GetValue(model);
            void Command(string property) => ((ICommand)Get(property)!).Execute(null);
            var a = Path.Combine(report, "Workspace-A"); var b = Path.Combine(report, "Workspace-B"); var c = Path.Combine(report, "Workspace-C");
            foreach (var path in new[] { a, b, c }) Directory.CreateDirectory(path);
            // Use synthetic UI values only. Never save a profile, connect, or arm control.
            Set("ServerUrl", "https://jarvis.example.test"); Set("DeviceId", "11111111-1111-1111-1111-111111111111");
            Set("Workspace", a); ((PasswordBox)window.FindName("TokenBox")).Password = "";
            var folders = (ObservableCollection<string>)Get("AdditionalDirectories")!;
            folders.Clear(); folders.Add(b); folders.Add(c);
            Set("SelectedDirectory", b); Command("MakePrimaryDirectoryCommand");
            if ((string)Get("Workspace")! != b || !folders.Contains(a)) throw new InvalidOperationException("Make primary did not swap directories.");
            Set("SelectedDirectory", a); Command("RemoveDirectoryCommand");
            if (folders.Contains(a) || folders.Count != 1) throw new InvalidOperationException("Remove directory failed.");
            folders.Add(a);
            if (window.Icon is null) throw new InvalidOperationException("Window icon is not loaded.");
            var preShowSettingsTabs = window.FindName("SettingsTabs") as TabControl
                ?? throw new InvalidOperationException("Settings content host is missing.");
            if (preShowSettingsTabs.Focusable || KeyboardNavigation.GetIsTabStop(preShowSettingsTabs))
                throw new InvalidOperationException("Hidden settings content host must not be keyboard-focusable or a tab stop.");
            window.Show();

            window.UpdateLayout();

            var navigation = window.FindName("WorkspaceNavigation") as ListBox
                ?? throw new InvalidOperationException("Workspace navigation list is missing.");
            if (navigation.Items.Count != 5) throw new InvalidOperationException("Workspace navigation must expose exactly five destinations.");
            Set("SelectedTab", 1); window.UpdateLayout();
            if (navigation.SelectedIndex != 1) throw new InvalidOperationException("Sidebar selection did not follow SelectedTab.");
            navigation.SelectedIndex = 0; window.UpdateLayout();
            if ((int)Get("SelectedTab")! != 0) throw new InvalidOperationException("SelectedTab did not follow sidebar selection.");
            var settingsTabs = window.FindName("SettingsTabs") as TabControl
                ?? throw new InvalidOperationException("Settings content host is missing.");
            if (settingsTabs.Focusable || KeyboardNavigation.GetIsTabStop(settingsTabs))
                throw new InvalidOperationException("Hidden settings content host must not be keyboard-focusable or a tab stop.");
            bool ContainsTabHeaderPanel(DependencyObject root)
            {
                if (root is TabPanel) return true;
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                    if (ContainsTabHeaderPanel(VisualTreeHelper.GetChild(root, i))) return true;
                return false;
            }
            if (ContainsTabHeaderPanel(settingsTabs))
                throw new InvalidOperationException("Settings content host still renders a duplicate tab-header strip.");
            var versionText = window.FindName("DesktopVersionText") as TextBlock
                ?? throw new InvalidOperationException("Desktop version label is missing.");
            var expectedVersionLabel = $"Windows desktop \u00b7 v{assembly.GetName().Version!.ToString(3)}";
            if (!string.Equals(versionText.Text, expectedVersionLabel, StringComparison.Ordinal))
                throw new InvalidOperationException($"Desktop version label drifted: '{versionText.Text}' != '{expectedVersionLabel}'.");

            void Capture(string filename)
            {
                window.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(report, filename)); encoder.Save(output);
            }
            Capture("agent-connection.png");
            Command("ClearWorkspaceCommand");
            if ((string)Get("Workspace")! != "" || folders.Count != 0) throw new InvalidOperationException("Workspace default could not be left empty.");

            static object Property(object o, string name) => o.GetType().GetProperty(name)!.GetValue(o)!;
            static void Put(object o, string name, object value) => o.GetType().GetProperty(name)!.SetValue(o, value);
            static void Execute(object o, string name) => ((ICommand)Property(o, name)).Execute(null);
            var limits = Get("Execution")!; var concurrent = Property(limits, "ConcurrentCalls");
            Set("SelectedTab", 2); window.UpdateLayout();
            if (navigation.SelectedIndex != 2) throw new InvalidOperationException("Limits navigation failed.");
            Put(concurrent, "Text", "0"); Capture("agent-limits-invalid.png");
            if (((ICommand)Property(limits, "SaveCommand")).CanExecute(null)) throw new InvalidOperationException("Invalid concurrency may not be saved.");
            Put(concurrent, "Text", "8"); Execute(limits, "SaveCommand");
            if (!string.IsNullOrEmpty((string)Property(limits, "Error"))) throw new InvalidOperationException((string)Property(limits, "Error"));
            Execute(limits, "ReloadCommand");
            if ((string)Property(concurrent, "Text") != "8") throw new InvalidOperationException("Limits did not survive reload.");
            Capture("agent-limits.png");
            window.Width = 870; window.Height = 650; Capture("agent-limits-minimum.png");
            Set("SelectedTab", 3); Capture("agent-sessions-empty.png");
            if (navigation.SelectedIndex != 3) throw new InvalidOperationException("Sessions navigation failed.");

            // Synthetic metadata for layout only. No runtime/agent sessions are opened.
            var protocol = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(publish, "Jarvis.Protocol.dll"));
            var overviewType = protocol.GetType("Jarvis.Protocol.LocalSessionOverview", true)!;
            var identityType = protocol.GetType("Jarvis.Protocol.AgentSessionIdentity", true)!;
            var snapshotType = protocol.GetType("Jarvis.Protocol.AgentSessionSnapshot", true)!;
            var activityType = protocol.GetType("Jarvis.Protocol.AgentSessionActivity", true)!;
            var previews = Array.CreateInstance(overviewType, 3);
            for (var i=0; i<3; i++)
            {
                var id = "js_" + (i + 1).ToString("x32");
                var identity = Activator.CreateInstance(identityType, ["synthetic-owner", "synthetic-device", id])!;
                var snap = Activator.CreateInstance(snapshotType, [id, "synthetic-device", new[] { "Build and review", "Documentation", "Browser validation" }[i],
                    i==1 ? "" : Path.Combine(report, "Workspace-" + new string('A', 90)), Array.Empty<string>(), (long)i,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, (long)i])!;
                var activity = Activator.CreateInstance(activityType)!;
                Put(activity, "RunningCalls", i==0 ? 2 : 0); Put(activity, "QueuedCalls", i==2 ? 3 : 0);
                Put(activity, "HeldResources", i==0 ? new[] {"fs|"+a} : Array.Empty<string>());
                previews.SetValue(Activator.CreateInstance(overviewType, [identity, snap, activity]), i);
            }
            var queryResult = typeof(IReadOnlyList<>).MakeGenericType(overviewType);
            var query = Expression.Lambda(typeof(Func<>).MakeGenericType(queryResult), Expression.Convert(Expression.Constant(previews), queryResult)).Compile();
            var idParameter = Expression.Parameter(identityType); var closeParameter = Expression.Parameter(typeof(bool));
            var stop = Expression.Lambda(typeof(Action<,>).MakeGenericType(identityType, typeof(bool)), Expression.Empty(), idParameter, closeParameter).Compile();
            var sessionsType = assembly.GetType("Jarvis.Agent.Desktop.ViewModels.SessionsViewModel", true)!;
            var previewModel = Activator.CreateInstance(sessionsType, [query, (Func<bool>)(() => true), stop, null])!;
            Execute(previewModel, "RefreshCommand");
            var previewItems = ((IEnumerable)Property(previewModel, "Items")).Cast<object>().ToArray();
            Put(previewModel, "Selected", previewItems[0]);
            static IEnumerable<DependencyObject> Descendants(DependencyObject root)
            {
                for (var i=0; i<VisualTreeHelper.GetChildrenCount(root); i++)
                { var child=VisualTreeHelper.GetChild(root,i); yield return child; foreach(var descendant in Descendants(child)) yield return descendant; }
            }
            var sessionsView = (FrameworkElement)Descendants(window).Single(v => v.GetType().Name == "SessionsView");
            sessionsView.DataContext = previewModel;
            Capture("agent-sessions-minimum.png");
            var bulkChecks = Descendants(sessionsView).OfType<CheckBox>()
                .Where(box => Equals(box.ToolTip, "Select this session for Stop selected or Close selected")).ToArray();
            if (bulkChecks.Length != 3) throw new InvalidOperationException("Session rows must expose one bulk-selection checkbox each.");
            Execute(previewModel, "SelectAllCommand"); window.UpdateLayout();
            if ((int)Property(previewModel, "BulkSelectedCount") != 3 || bulkChecks.Any(box => box.IsChecked != true))
                throw new InvalidOperationException("Select all did not check every visible open session.");
            Capture("agent-sessions-bulk-selected.png");
            Execute(previewModel, "ClearSelectionCommand"); window.UpdateLayout();
            if ((int)Property(previewModel, "BulkSelectedCount") != 0 || bulkChecks.Any(box => box.IsChecked == true))
                throw new InvalidOperationException("Clear selection did not reset the bulk selection.");
            var selectableSessionText = Descendants(sessionsView).OfType<TextBox>().Where(box => box.IsReadOnly).ToArray();
            var selectableLabel = selectableSessionText.FirstOrDefault(box => box.Text == "Build and review");
            var selectableId = selectableSessionText.FirstOrDefault(box => box.Text.StartsWith("js_", StringComparison.Ordinal));
            if (selectableLabel is null || selectableId is null) throw new InvalidOperationException("Session rows must expose selectable name and ID text.");
            if (KeyboardNavigation.GetIsTabStop(selectableLabel) || KeyboardNavigation.GetIsTabStop(selectableId))
                throw new InvalidOperationException("Selectable session identity text must not add redundant tab stops.");
            selectableLabel.SelectAll(); selectableId.SelectAll();
            if (selectableLabel.SelectedText != selectableLabel.Text || selectableId.SelectedText != selectableId.Text)
                throw new InvalidOperationException("Session identity text could not be selected for copy.");
            var details = Descendants(sessionsView).OfType<Expander>().Single(); details.IsExpanded = true;
            Capture("agent-sessions-minimum-details.png");
            window.Width=1160; window.Height=820; Capture("agent-sessions.png");
            Put(previewModel, "Search", "no-such-synthetic-session"); Capture("agent-sessions-filter-empty.png");
            if (((IEnumerable)Property(previewModel, "Items")).Cast<object>().Any()) throw new InvalidOperationException("Session filter did not apply.");
            if (bindingErrors.Messages.Count != 0) throw new InvalidOperationException("WPF binding errors: " + string.Join(" | ", bindingErrors.Messages));


            PromptUiSmoke.Run(window, model, report, Capture);
            if (bindingErrors.Messages.Count != 0) throw new InvalidOperationException("WPF binding errors: " + string.Join(" | ", bindingErrors.Messages));
            if (promptsOnly)
            {
                Console.WriteLine(File.ReadAllText(Path.Combine(report, "prompt-ui-smoke.json")));
                return 0;
            }
            var permissions = Get("Permissions")!; var permissionsType = permissions.GetType();
            object? PermissionGet(string property) => permissionsType.GetProperty(property)!.GetValue(permissions);
            void PermissionCommand(string name) => ((ICommand)PermissionGet(name)!).Execute(null);
            var items = ((IEnumerable)PermissionGet("Items")!).Cast<object>().ToArray();
            if (items.Length < 20) throw new InvalidOperationException("Offline inventory did not load.");
            bool Selected(object item) => (bool)item.GetType().GetProperty("FullPermission")!.GetValue(item)!;
            if (items.Any(Selected)) throw new InvalidOperationException("A fresh profile must not auto-grant tools.");
            var first = items[0]; first.GetType().GetProperty("FullPermission")!.SetValue(first, true);
            PermissionCommand("SaveCommand");
            if (!string.IsNullOrEmpty((string)PermissionGet("Error")!)) throw new InvalidOperationException((string)PermissionGet("Error")!);
            var permissionFile = Path.Combine(settingsRoot, "tool-permissions.json");
            string[] savedFullPermissions;
            using (var saved = JsonDocument.Parse(File.ReadAllText(permissionFile)))
            {
                if (saved.RootElement.GetProperty("version").GetInt32() != 2) throw new InvalidOperationException("Permission settings did not migrate to v2.");
                if (saved.RootElement.GetProperty("fullPermissionTools").GetArrayLength() != 1) throw new InvalidOperationException("Single-tool save failed.");
                if (saved.RootElement.GetProperty("alwaysApprovedConstrainedTools").GetArrayLength() != 0) throw new InvalidOperationException("Fresh permission save unexpectedly granted permanent process approval.");
                savedFullPermissions = saved.RootElement.GetProperty("fullPermissionTools").EnumerateArray().Select(value => value.GetString()!).ToArray();
            }
            File.WriteAllText(permissionFile, JsonSerializer.Serialize(new { version = 2, fullPermissionTools = savedFullPermissions,
                alwaysApprovedConstrainedTools = new[] { "process.start" } }, new JsonSerializerOptions { WriteIndented = true }));
            // Reload the real view model using isolated settings only, never the current user's profile.
            var reloaded = Activator.CreateInstance(modelType, [window, settingsRoot, false])!;
            var reloadedPermissions = modelType.GetProperty("Permissions")!.GetValue(reloaded)!;
            var reloadedItems = ((IEnumerable)reloadedPermissions.GetType().GetProperty("Items")!.GetValue(reloadedPermissions)!).Cast<object>().ToArray();
            if (reloadedItems.Count(Selected) != 1) throw new InvalidOperationException("Permission persistence reload failed.");
            var processStart = reloadedItems.Single(item => (string)item.GetType().GetProperty("Id")!.GetValue(item)! == "process.start");
            if (!(bool)processStart.GetType().GetProperty("AlwaysApproved")!.GetValue(processStart)!) throw new InvalidOperationException("Persistent process approval did not reload into Tool permissions.");
            ((ICommand)processStart.GetType().GetProperty("RevokeAlwaysApprovalCommand")!.GetValue(processStart)!).Execute(null);
            if ((bool)processStart.GetType().GetProperty("AlwaysApproved")!.GetValue(processStart)!) throw new InvalidOperationException("Require approval again did not clear UI state.");
            using (var revoked = JsonDocument.Parse(File.ReadAllText(permissionFile)))
                if (revoked.RootElement.GetProperty("alwaysApprovedConstrainedTools").GetArrayLength() != 0) throw new InvalidOperationException("Require approval again did not persist revocation.");
            ((IAsyncDisposable)reloaded).DisposeAsync().AsTask().GetAwaiter().GetResult();
            permissionsType.GetProperty("Search")!.SetValue(permissions, "PowerShell");
            PermissionCommand("SelectAllCommand");
            if (items.Count(Selected) != items.Length) throw new InvalidOperationException("Select all did not include filtered-out tools.");
            // Draft changes have not changed the persisted selection.
            using (var saved = JsonDocument.Parse(File.ReadAllText(permissionFile)))
                if (saved.RootElement.GetProperty("fullPermissionTools").GetArrayLength() != 1) throw new InvalidOperationException("Draft selection was applied before Save.");
            PermissionCommand("ResetCommand");
            if (items.Count(Selected) != 1) throw new InvalidOperationException("Reset did not restore active permissions.");
            permissionsType.GetProperty("Search")!.SetValue(permissions, "");
            PermissionCommand("SelectAllCommand"); PermissionCommand("SaveCommand");
            Set("SelectedTab", 1); Capture("agent-permissions.png");
            PermissionCommand("ClearAllCommand"); PermissionCommand("SaveCommand");
            using (var saved = JsonDocument.Parse(File.ReadAllText(permissionFile)))
                if (saved.RootElement.GetProperty("fullPermissionTools").GetArrayLength() != 0) throw new InvalidOperationException("Clear all did not revoke the saved selection.");
            var result = new { version = assembly.GetName().Version!.ToString(), windowRendered = true,
                iconLoaded = true, makePrimaryPassed = true, removeDirectoryPassed = true,
                settingsSidebarNavigationPassed = true, runtimeVersionLabelPassed = true,
                installedTools = items.Length, permissionTabRendered = true, singleToolSave = true,
                reloadPersistence = true, permissionSettingsV2 = true, alwaysApprovalReload = true, alwaysApprovalRevoke = true,
                selectAllIncludesFilteredOut = true, draftDoesNotApplyBeforeSave = true,
                resetRestoresSaved = true, clearAllRevokes = true, isolatedPermissionSettingsOnly = true,
                executionLimitsRendered = true, invalidLimitsBlocked = true, executionLimitsReload = true,
                optionalWorkspaceCleared = true, sessionMetadataRendered = true, sessionIdentitySelectable = true, emptyAndFilteredStates = true,
                minimumWindowSizeRendered = true, bindingErrors = bindingErrors.Messages.Count,
                profileSaved = false, connectionStarted = false, controlArmed = false };
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(report, "ui-smoke.json"), json); Console.WriteLine(json);

            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            if (window is not null)
            {
                var method = window.GetType().GetMethod("ExitAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
                ((Task)method.Invoke(window, null)!).GetAwaiter().GetResult();
            }
        }
    }
}
