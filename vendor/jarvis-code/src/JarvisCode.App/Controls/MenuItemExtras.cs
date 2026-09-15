using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace JarvisCode.App.Controls;

/// <summary>
/// The slot the reference's menu row has and WPF's <see cref="MenuItem"/> does not:
/// content wedged into the trailing cluster ahead of the check glyph and the shortcut
/// keycap, which its permission picker fills with the "Default" badge.
/// </summary>
public static class MenuItemExtras
{
    public static readonly DependencyProperty TrailingProperty =
        DependencyProperty.RegisterAttached(
            "Trailing", typeof(object), typeof(MenuItemExtras), new PropertyMetadata(null));

    public static object? GetTrailing(DependencyObject element) => element.GetValue(TrailingProperty);

    public static void SetTrailing(DependencyObject element, object? value) => element.SetValue(TrailingProperty, value);

    /// <summary>
    /// The row's corner radius. The reference reads it from the density in force — 6
    /// at compact, 8 at comfortable — and this app's menus are compact everywhere
    /// except the two popups ported at the comfortable density the reference's own
    /// composer menus use.
    /// </summary>
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.RegisterAttached(
            "CornerRadius", typeof(CornerRadius), typeof(MenuItemExtras),
            new PropertyMetadata(new CornerRadius(6)));

    public static CornerRadius GetCornerRadius(DependencyObject element) =>
        (CornerRadius)element.GetValue(CornerRadiusProperty);

    public static void SetCornerRadius(DependencyObject element, CornerRadius value) =>
        element.SetValue(CornerRadiusProperty, value);
}

/// <summary>
/// A menu row's shortcut split into the keys it is made of, because the reference
/// draws one cap per key rather than one cap around the whole chord. Both spellings
/// this app writes are accepted: "Ctrl+C" and the spaced "Ctrl ⇧ Y".
/// </summary>
public sealed class ShortcutKeysConverter : IValueConverter
{
    private static readonly char[] Separators = ['+', ' '];

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text && !string.IsNullOrWhiteSpace(text)
            ? text.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
