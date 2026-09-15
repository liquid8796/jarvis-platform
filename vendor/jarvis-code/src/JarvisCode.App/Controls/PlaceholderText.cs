using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace JarvisCode.App.Controls;

/// <summary>
/// The hint a field shows while it is empty. WPF has no placeholder, so this app drew one
/// as a label over the box — and a label over a box drifts from the text it stands in for,
/// because every offset was typed rather than derived: a margin of 12 over a field whose
/// text starts at its own 1px border plus 10px of padding, a hint at 13 where the box sets
/// 14, a hint anchored to the top of a box whose content host is centred. This carries the
/// text instead, and the field draws it from its <em>own</em> padding, border and font, so
/// the placeholder and the caret start at the same point by construction rather than by
/// two numbers agreeing.
/// </summary>
public static class PlaceholderText
{
    /// <summary>
    /// How far inside its padding box WPF draws a field's first character: the room
    /// TextBoxView keeps for the caret at the start of a line. Measured on every field
    /// in the app by <c>--input-selftest</c> — 2 device-independent pixels, identical at
    /// every font size and padding — and a placeholder has to carry it too, or it stands
    /// two pixels to the left of the text it stands in for.
    /// </summary>
    public const double TextInset = 2;

    /// <summary>The hint text; empty or null draws nothing.</summary>
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached(
            "Text", typeof(string), typeof(PlaceholderText),
            new FrameworkPropertyMetadata(null, OnTextChanged));

    /// <summary>Whether this field's template hook has already been wired.</summary>
    private static readonly DependencyProperty HookedProperty =
        DependencyProperty.RegisterAttached(
            "Hooked", typeof(bool), typeof(PlaceholderText), new PropertyMetadata(false));

    public static void SetText(DependencyObject element, string? value) =>
        element.SetValue(TextProperty, value);

    public static string? GetText(DependencyObject element) =>
        (string?)element.GetValue(TextProperty);

    /// <summary>
    /// The text is pushed into the template's own <c>Placeholder</c> part rather than
    /// bound from it. A template can only reach an attached property through a
    /// parenthesised path — <c>{Binding (PlaceholderText.Text), RelativeSource=
    /// {RelativeSource TemplatedParent}}</c> — and that path resolves against the parser
    /// context rather than the property system: measured on the laid-out tree, the fields
    /// templated earliest in a run come up <c>PathError</c> and draw no hint at all while
    /// later ones bind fine, so a field's placeholder appeared or did not by the order the
    /// app happened to build its screens in. Pushing the value cannot fail that way.
    /// </summary>
    private static void OnTextChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Control field)
        {
            return;
        }

        if (!(bool)field.GetValue(HookedProperty))
        {
            field.SetValue(HookedProperty, true);
            // The template is applied when the field joins a tree, which may be long
            // after the text was set, so the push is repeated there.
            field.Loaded += (sender, _) => Push((Control)sender);
        }

        Push(field);
    }

    /// <summary>
    /// Writes the current hint into the field's template part. Only a field something has
    /// set the property on is ever pushed to, so a template that carries its own default
    /// hint keeps it, and clearing the property really does clear the label.
    /// </summary>
    private static void Push(Control field)
    {
        field.ApplyTemplate();
        if (field.Template?.FindName("Placeholder", field) is TextBlock hint)
        {
            hint.Text = GetText(field) ?? "";
        }
    }

    /// <summary>
    /// A hint for a box whose template has no placeholder slot of its own — the composer,
    /// the quick-entry pill, the side chat. The label takes every measurement from the box
    /// (the inset its border and padding make, its font, how it wraps, how it aligns), so
    /// the two cannot drift; the caller only has to drop it in a <see cref="Panel"/> over
    /// the box.
    /// </summary>
    public static TextBlock Overlay(TextBox box, string text, string foregroundKey = "Text500Brush")
    {
        var hint = new TextBlock
        {
            Text = text,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = box.AcceptsReturn ? VerticalAlignment.Top : VerticalAlignment.Center,
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, foregroundKey);
        Bind(hint, TextBlock.MarginProperty, InsetBinding(box));
        Bind(hint, TextBlock.FontFamilyProperty, Follow(box, "FontFamily"));
        Bind(hint, TextBlock.FontSizeProperty, Follow(box, "FontSize"));
        Bind(hint, TextBlock.FontStyleProperty, Follow(box, "FontStyle"));
        Bind(hint, TextBlock.FontWeightProperty, Follow(box, "FontWeight"));
        Bind(hint, TextBlock.FontStretchProperty, Follow(box, "FontStretch"));
        Bind(hint, TextBlock.TextWrappingProperty, Follow(box, "TextWrapping"));
        Bind(hint, TextBlock.TextAlignmentProperty, Follow(box, "TextAlignment"));

        void Sync() => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Sync();
        Sync();
        return hint;
    }

    /// <summary>The gap between a field's outer edge and the first character it draws.</summary>
    private static MultiBinding InsetBinding(TextBox box)
    {
        var binding = new MultiBinding { Converter = PlaceholderFieldInset.Instance };
        binding.Bindings.Add(Follow(box, "Padding"));
        binding.Bindings.Add(Follow(box, "BorderThickness"));
        return binding;
    }

    private static Binding Follow(TextBox box, string path) =>
        new(path) { Source = box, Mode = BindingMode.OneWay };

    private static void Bind(TextBlock hint, DependencyProperty property, BindingBase binding) =>
        BindingOperations.SetBinding(hint, property, binding);

}

/// <summary>Padding + border + the caret's own room, which is where a text box starts drawing.</summary>
public sealed class PlaceholderFieldInset : IMultiValueConverter
{
    public static readonly PlaceholderFieldInset Instance = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var padding = values.ElementAtOrDefault(0) as Thickness? ?? default;
        var border = values.ElementAtOrDefault(1) as Thickness? ?? default;
        return new Thickness(
            padding.Left + border.Left + PlaceholderText.TextInset,
            padding.Top + border.Top,
            padding.Right + border.Right,
            padding.Bottom + border.Bottom);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A field template's own inset: the padding it is given, plus the room WPF keeps for
/// the caret. The border is not added here — inside a template the placeholder already
/// sits within it.
/// </summary>
public sealed class PlaceholderInset : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var padding = value as Thickness? ?? default;
        return new Thickness(
            padding.Left + PlaceholderText.TextInset, padding.Top, padding.Right, padding.Bottom);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A child drawn over its siblings without taking part in their measure. A placeholder
/// inside a field's own template would otherwise widen the field to fit the hint — an
/// auto-sized box would grow to whatever sentence it happens to advertise — so the hint
/// is measured against the room it is given and reports none of its own.
/// </summary>
public sealed class OverlayHost : Decorator
{
    protected override Size MeasureOverride(Size constraint)
    {
        Child?.Measure(constraint);
        return new Size(0, 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(finalSize));
        return finalSize;
    }
}
