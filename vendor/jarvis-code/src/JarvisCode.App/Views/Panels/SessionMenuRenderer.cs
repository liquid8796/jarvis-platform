using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views.Panels;

/// <summary>
/// Draws a <see cref="SessionMenuModel"/> row list as a WPF menu. The reference
/// renders both of its session menus from one row array (`ry` in the ccd chunk), and
/// so does this: the sidebar row's menu, the session header's and the Recents
/// header's multi-select share the renderer, which is what keeps a row's letter, its
/// radio mark and its danger tint the same wherever it appears.
/// </summary>
public static class SessionMenuRenderer
{
    /// <summary>Builds the popup for <paramref name="rows"/>; <paramref name="invoke"/> runs the picked row.</summary>
    public static ContextMenu Build(IReadOnlyList<SessionMenuRow> rows, Action<SessionMenuRow> invoke)
    {
        var menu = new ContextMenu { MinWidth = 200 };
        Fill(menu.Items, rows, invoke, menu);
        menu.PreviewKeyDown += (_, e) => OnKey(menu, rows, invoke, e);
        return menu;
    }

    /// <summary>Adds the rows to an existing item collection (a submenu, or a menu being extended).</summary>
    public static void Fill(ItemCollection items, IReadOnlyList<SessionMenuRow> rows, Action<SessionMenuRow> invoke,
        ContextMenu owner)
    {
        foreach (var row in rows)
        {
            switch (row.Kind)
            {
                case SessionMenuRowKind.Separator:
                    items.Add(new Separator());
                    break;
                case SessionMenuRowKind.Submenu:
                    items.Add(BuildSubmenu(row, invoke, owner));
                    break;
                default:
                    items.Add(BuildItem(row, invoke, owner));
                    break;
            }
        }
    }

    private static MenuItem BuildSubmenu(SessionMenuRow row, Action<SessionMenuRow> invoke, ContextMenu owner)
    {
        var item = new MenuItem { Header = row.Label };
        var children = row.Children ?? [];
        Fill(item.Items, children, invoke, owner);

        // The reference's submenus answer to digits while they are open, first nine rows only.
        item.PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.None || e.Key is < Key.D1 or > Key.D9)
            {
                return;
            }

            var digit = ((int)e.Key - (int)Key.D0).ToString();
            if (children.FirstOrDefault(r => r.Kind == SessionMenuRowKind.Item && !r.Disabled && r.Shortcut == digit)
                is { } picked)
            {
                e.Handled = true;
                owner.IsOpen = false;
                invoke(picked);
            }
        };
        return item;
    }

    private static MenuItem BuildItem(SessionMenuRow row, Action<SessionMenuRow> invoke, ContextMenu owner)
    {
        var item = new MenuItem
        {
            Header = row.Label,
            IsEnabled = !row.Disabled,
            InputGestureText = row.Shortcut is { Length: 1 } letter
                ? char.IsDigit(letter[0]) ? "" : letter.ToUpperInvariant()
                : row.Shortcut ?? "",
        };

        if (row.Icon is { Length: > 0 } icon)
        {
            item.Icon = Glyph(icon);
        }

        if (row.Checked is { } isChecked)
        {
            // The reference marks a radio row with a tick in the icon slot and leaves
            // an unchecked one blank, rather than drawing an empty check box.
            item.IsCheckable = false;
            item.Icon = isChecked ? Tick() : null;
        }

        if (row.Danger)
        {
            item.SetResourceReference(FrameworkElement.StyleProperty, "DangerMenuItem");
        }

        if (row.Description is { Length: > 0 } description)
        {
            item.Header = Stacked(row.Label, description);
        }

        item.Click += (_, _) =>
        {
            owner.IsOpen = false;
            invoke(row);
        };
        return item;
    }

    /// <summary>A pane row's icon, drawn from the theme's own path geometry.</summary>
    private static FrameworkElement Glyph(string key)
    {
        var path = new System.Windows.Shapes.Path
        {
            Width = 15,
            Height = 15,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        path.SetResourceReference(System.Windows.Shapes.Path.DataProperty, key);
        path.SetResourceReference(Shape.StrokeProperty, "Text300Brush");
        return path;
    }

    private static FrameworkElement Tick()
    {
        var path = new System.Windows.Shapes.Path
        {
            Width = 12,
            Height = 12,
            Stretch = Stretch.Uniform,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Data = Geometry.Parse("M 2 8 L 6 12 L 14 3"),
        };
        path.SetResourceReference(Shape.StrokeProperty, "AccentBrandBrush");
        return path;
    }

    /// <summary>A menu row with a dimmed second line, the way the reference's "Cloud" row carries one.</summary>
    private static FrameworkElement Stacked(string title, string description)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = title });
        var caption = new TextBlock { Text = description, FontSize = 11.5, Margin = new Thickness(0, 1, 0, 0) };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
        stack.Children.Add(caption);
        return stack;
    }

    /// <summary>
    /// The reference's letter accelerators (`wn`/`Tn`): a bare single character with
    /// no modifier runs the first enabled row that answers to it.
    /// </summary>
    private static void OnKey(ContextMenu menu, IReadOnlyList<SessionMenuRow> rows, Action<SessionMenuRow> invoke,
        KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        var text = KeyText(e.Key);
        if (text is null || SessionMenuModel.ForShortcut(rows, text.Value) is not { } row)
        {
            return;
        }

        e.Handled = true;
        menu.IsOpen = false;
        invoke(row);
    }

    private static char? KeyText(Key key) => key is >= Key.A and <= Key.Z
        ? (char)('a' + (key - Key.A))
        : null;
}
