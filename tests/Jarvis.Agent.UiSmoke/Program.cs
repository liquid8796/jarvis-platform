using System;
using System.IO;
using System.Collections.ObjectModel;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("Usage: UiSmoke <published-desktop-directory> <report-directory>"); return 2; }
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
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(publish, "Jarvis.Agent.Desktop.dll"));
            var appType = assembly.GetType("Jarvis.Agent.Desktop.App", throwOnError: true)!;
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
            if (navigation.Items.Count != 2) throw new InvalidOperationException("Workspace navigation must expose exactly two destinations.");
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
            var expectedVersionLabel = $"Windows desktop · v{assembly.GetName().Version!.ToString(3)}";
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
            using (var saved = JsonDocument.Parse(File.ReadAllText(permissionFile)))
                if (saved.RootElement.GetProperty("fullPermissionTools").GetArrayLength() != 1) throw new InvalidOperationException("Single-tool save failed.");
            // Reload the real view model using isolated settings only, never the current user's profile.
            var reloaded = Activator.CreateInstance(modelType, [window, settingsRoot, false])!;
            var reloadedPermissions = modelType.GetProperty("Permissions")!.GetValue(reloaded)!;
            var reloadedItems = ((IEnumerable)reloadedPermissions.GetType().GetProperty("Items")!.GetValue(reloadedPermissions)!).Cast<object>();
            if (reloadedItems.Count(Selected) != 1) throw new InvalidOperationException("Permission persistence reload failed.");
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
                reloadPersistence = true, selectAllIncludesFilteredOut = true, draftDoesNotApplyBeforeSave = true,
                resetRestoresSaved = true, clearAllRevokes = true, isolatedPermissionSettingsOnly = true,
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
