using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JarvisCode.App.Controls;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views.Settings;

/// <summary>
/// The code-theme preview the reference draws under each theme select: its
/// <c>greet.ts</c> before/after files rendered as a unified diff with classic
/// indicators, no file header and wrapped lines (<c>c71860c77-D1Dqt2F3.js</c>,
/// its <c>Sa</c>/<c>Aa</c> and the <c>diffStyle:"unified", diffIndicators:"classic",
/// disableFileHeader:true, overflow:"wrap"</c> options), painted with the theme's
/// own colours.
/// </summary>
internal static class CodePreviewCard
{
    public static Border Build(CodeTheme theme, string codeFont)
    {
        var lines = new StackPanel();
        var oldLines = CodeThemes.PreviewOld.TrimEnd('\n').Split('\n');
        var newLines = CodeThemes.PreviewNew.TrimEnd('\n').Split('\n');

        // Line 1 and 3 are unchanged, line 2 was rewritten: one removal, one addition.
        lines.Children.Add(Line(theme, codeFont, " ", oldLines[0], null));
        lines.Children.Add(Line(theme, codeFont, "-", oldLines[1], Color.FromArgb(0x2E, 0xFF, 0x3A, 0x30)));
        lines.Children.Add(Line(theme, codeFont, "+", newLines[1], Color.FromArgb(0x2E, 0x32, 0xD7, 0x4B)));
        lines.Children.Add(Line(theme, codeFont, " ", oldLines[2], null));

        var card = new Border
        {
            Child = lines,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0, 6, 0, 6),
            ClipToBounds = true,
        };
        card.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");
        if (theme.Color(theme.Background) is { } bg)
        {
            card.Background = new SolidColorBrush(bg);
        }
        else
        {
            card.SetResourceReference(Border.BackgroundProperty, "Bg000Brush");
        }

        return card;
    }

    private static FrameworkElement Line(CodeTheme theme, string codeFont, string indicator, string code, Color? tint)
    {
        var block = new TextBlock
        {
            FontSize = 12,
            Padding = new Thickness(10, 1, 10, 1),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = codeFont.Length > 0
                ? new FontFamily(codeFont + ", Cascadia Mono, Consolas")
                : (FontFamily)Application.Current.Resources["MonoFontFamily"],
        };
        if (tint is { } color)
        {
            block.Background = new SolidColorBrush(color);
        }

        var marker = new System.Windows.Documents.Run(indicator + " ");
        marker.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Text400Brush");
        block.Inlines.Add(marker);

        foreach (var span in SyntaxHighlighter.Highlight("typescript", code))
        {
            var run = new System.Windows.Documents.Run(span.Text);
            var hex = span.Kind switch
            {
                SyntaxKind.Comment => theme.Comment,
                SyntaxKind.String => theme.String,
                SyntaxKind.Keyword => theme.Keyword,
                SyntaxKind.Number => theme.Number,
                SyntaxKind.Variable => theme.Variable,
                _ => theme.Foreground,
            };
            if (theme.Color(hex) is { } fg)
            {
                run.Foreground = new SolidColorBrush(fg);
            }
            else
            {
                run.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, CodeText.BrushKeyFor(span.Kind));
            }

            block.Inlines.Add(run);
        }

        return block;
    }
}
