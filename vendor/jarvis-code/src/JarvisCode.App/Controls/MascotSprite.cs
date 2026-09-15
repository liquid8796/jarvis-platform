using System.Windows;
using System.Windows.Media;

namespace JarvisCode.App.Controls;

/// <summary>
/// The little pixel-art mascot that perches on the context bar — an original
/// 8-bit critter drawn in the accent color (the reference app has one too).
/// </summary>
public sealed class MascotSprite : FrameworkElement
{
    // 17×13 pixel map; '#' = accent, '.' = empty, 'o' = accent at 45%.
    private static readonly string[] Pixels =
    [
        "..##.........##..",
        "..#...........#..",
        "..#############..",
        ".###############.",
        ".##.###...###.##.",
        ".##.###...###.##.",
        ".###############.",
        "..####.o.o.####..",
        "..#############..",
        "....##..#..##....",
        "...##...#...##...",
        "...#....#....#...",
        "..##...###...##..",
    ];

    public MascotSprite()
    {
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var accent = Application.Current?.Resources["AccentBrandColor"] is Color c ? c : Colors.Coral;
        var solid = new SolidColorBrush(accent);
        var soft = new SolidColorBrush(Color.FromArgb(115, accent.R, accent.G, accent.B));
        solid.Freeze();
        soft.Freeze();

        var cols = Pixels[0].Length;
        var rows = Pixels.Length;
        var cell = Math.Min(ActualWidth / cols, ActualHeight / rows);
        var offsetX = (ActualWidth - cols * cell) / 2;
        var offsetY = ActualHeight - rows * cell;

        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < cols; x++)
            {
                var ch = Pixels[y][x];
                if (ch == '.')
                {
                    continue;
                }

                dc.DrawRectangle(
                    ch == 'o' ? soft : solid,
                    null,
                    new Rect(offsetX + x * cell, offsetY + y * cell, cell + 0.5, cell + 0.5));
            }
        }
    }
}
