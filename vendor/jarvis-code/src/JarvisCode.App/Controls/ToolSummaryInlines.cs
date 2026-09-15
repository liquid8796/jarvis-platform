using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using JarvisCode.App.Services;

namespace JarvisCode.App.Controls;

/// <summary>
/// Draws a tool group's header sentence into a TextBlock, one clause at a time.
///
/// The clauses cannot be one bound string: the reference colours a clause whose
/// calls all failed ("edited 2 files, <span class="text-danger">ran 2
/// commands</span>") and leaves the rest in the header's own colour, and it
/// capitalises only the first. A collection of Runs is the WPF shape for that,
/// and an attached property is what lets the template stay declarative.
/// </summary>
public static class ToolSummaryInlines
{
    public static readonly DependencyProperty SegmentsProperty =
        DependencyProperty.RegisterAttached(
            "Segments",
            typeof(IReadOnlyList<ToolSummarySegment>),
            typeof(ToolSummaryInlines),
            new PropertyMetadata(null, OnSegmentsChanged));

    public static void SetSegments(DependencyObject element, IReadOnlyList<ToolSummarySegment>? value) =>
        element.SetValue(SegmentsProperty, value);

    public static IReadOnlyList<ToolSummarySegment>? GetSegments(DependencyObject element) =>
        (IReadOnlyList<ToolSummarySegment>?)element.GetValue(SegmentsProperty);

    private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }

        block.Inlines.Clear();
        if (e.NewValue is not IReadOnlyList<ToolSummarySegment> segments)
        {
            return;
        }

        for (int i = 0; i < segments.Count; i++)
        {
            if (i > 0)
            {
                block.Inlines.Add(new Run(", "));
            }

            var text = i == 0 ? ToolGroupSummary.Capitalize(segments[i].Text) : segments[i].Text;
            var run = new Run(text);
            if (segments[i].IsError)
            {
                run.SetResourceReference(TextElement.ForegroundProperty, "Danger100Brush");
            }

            block.Inlines.Add(run);
        }
    }
}
