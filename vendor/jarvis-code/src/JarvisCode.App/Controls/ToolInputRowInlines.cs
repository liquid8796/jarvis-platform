using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using JarvisCode.App.Services;

namespace JarvisCode.App.Controls;

/// <summary>
/// Draws one argument of an expanded tool row: the key in the code face at the
/// reference's own 70% opacity, a space, then the value in whichever face its
/// key earns — a path as a chip that opens the file, a command or a pattern in
/// the code face, anything else as the sentence it is.
///
/// It is an attached property rather than a template because the three cases
/// differ in their <em>inlines</em>, and a Run cannot be shown or hidden.
/// </summary>
public static class ToolInputRowInlines
{
    public static readonly DependencyProperty RowProperty =
        DependencyProperty.RegisterAttached(
            "Row",
            typeof(object),
            typeof(ToolInputRowInlines),
            new PropertyMetadata(null, OnRowChanged));

    public static void SetRow(DependencyObject element, object? value) =>
        element.SetValue(RowProperty, value);

    public static object? GetRow(DependencyObject element) => element.GetValue(RowProperty);

    private static void OnRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }

        block.Inlines.Clear();
        if (e.NewValue is not ToolInputRow row)
        {
            return;
        }

        var label = new Run(row.Label) { FontSize = 11.5 };
        label.SetResourceReference(TextElement.FontFamilyProperty, "MonoFontFamily");
        label.SetResourceReference(TextElement.ForegroundProperty, "Text500Brush");
        block.Inlines.Add(label);
        block.Inlines.Add(new Run(" "));

        if (row.IsFileRef)
        {
            var link = new Hyperlink(new Run(row.Value)) { Cursor = Cursors.Hand };
            link.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
            link.SetResourceReference(TextElement.FontFamilyProperty, "MonoFontFamily");
            link.FontSize = 11.5;
            var path = row.Value;
            link.Click += (_, _) => MarkdownView.FileActivationRequested?.Invoke(path, null);
            block.Inlines.Add(link);
            return;
        }

        var value = new Run(row.Value);
        if (row.IsCode)
        {
            value.SetResourceReference(TextElement.FontFamilyProperty, "MonoFontFamily");
            value.FontSize = 11.5;
        }

        block.Inlines.Add(value);
    }
}
