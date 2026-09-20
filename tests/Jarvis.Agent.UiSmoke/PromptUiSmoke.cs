using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

internal static class PromptUiSmoke
{
    public static void Run(Window window, object model, string report, Action<string> capture)
    {
        static object Get(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target)!;
        static void Set(object target, string name, object value) => target.GetType().GetProperty(name)!.SetValue(target, value);
        static void Execute(object target, string name) => ((ICommand)Get(target, name)).Execute(null);
        Set(model, "SelectedTab", 4); window.UpdateLayout();
        if (((ListBox)window.FindName("WorkspaceNavigation")).SelectedIndex != 4)
            throw new InvalidOperationException("Prompt navigation did not select the fifth destination.");
        var vm = Get(model, "PromptInjection");
        var rows = ((IEnumerable)Get(vm, "Items")).Cast<object>().ToArray();
        if (rows.Length != 7 || (bool)Get(vm, "Enabled") || rows.Any(row => (bool)Get(row, "Enabled")))
            throw new InvalidOperationException("Prompt defaults must be present but disabled.");
        var view = Descendants(window).OfType<FrameworkElement>().Single(v => v.GetType().Name == "PromptInjectionView");
        var inputs = Descendants(view).OfType<TextBox>().ToArray();
        var title = inputs.Single(box => AutomationProperties.GetName(box) == "Prompt title");
        var text = inputs.Single(box => AutomationProperties.GetName(box) == "Prompt text");
        var global = Descendants(view).OfType<CheckBox>().Single(box => AutomationProperties.GetName(box) == "Enable prompt injection");
        var selected = Descendants(view).OfType<CheckBox>().Single(box => AutomationProperties.GetName(box) == "Enable selected prompt");
        Execute(vm, "AddCommand"); window.UpdateLayout();
        title.SetCurrentValue(TextBox.TextProperty, "UI smoke example");
        text.SetCurrentValue(TextBox.TextProperty, "Explain observations and uncertainty. Synthetic test context only.");
        title.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        text.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        selected.SetCurrentValue(CheckBox.IsCheckedProperty, true);
        global.SetCurrentValue(CheckBox.IsCheckedProperty, true);
        selected.GetBindingExpression(CheckBox.IsCheckedProperty)!.UpdateSource();
        global.GetBindingExpression(CheckBox.IsCheckedProperty)!.UpdateSource();
        if ((string)Get(Get(vm, "Selected"), "Title") != "UI smoke example" || !(bool)Get(vm, "Enabled"))
            throw new InvalidOperationException("Prompt input bindings did not update the draft.");
        Execute(vm, "SaveCommand");
        if (!string.IsNullOrEmpty((string)Get(vm, "Error"))) throw new InvalidOperationException((string)Get(vm, "Error"));
        Execute(vm, "ReloadCommand");
        if (((IEnumerable)Get(vm, "Items")).Cast<object>().Count() != 8 || !(bool)Get(vm, "Enabled"))
            throw new InvalidOperationException("Prompt draft did not survive persistence and reload.");
        var expander = Descendants(view).OfType<Expander>().Single(); expander.IsExpanded = true;
        window.Width = 1160; window.Height = 820; capture("agent-prompts.png");
        window.Width = 870; window.Height = 650; capture("agent-prompts-minimum.png");
        Set(vm, "Enabled", false); Execute(vm, "SaveCommand");
        if (!string.IsNullOrEmpty((string)Get(vm, "Error"))) throw new InvalidOperationException((string)Get(vm, "Error"));
        var saved = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(report, "isolated-settings", "prompt-injection.json")));
        if (saved.GetProperty("enabled").GetBoolean()) throw new InvalidOperationException("Disabling prompt injection did not persist.");
        var result = new { promptTabRendered = true, defaultCount = 7, defaultsDisabled = true,
            titleAndTextBindings = true, toggleBindings = true, addAndSave = true, reload = true,
            disablePersisted = true, previewRendered = true, minimumSizeRendered = true,
            isolatedSettingsOnly = true, connectionStarted = false, controlArmed = false };
        File.WriteAllText(Path.Combine(report, "prompt-ui-smoke.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        window.Width = 1160; window.Height = 820;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
