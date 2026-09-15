using System.Text.Json;
using JarvisCode.Core.Markdown;

namespace JarvisCode.App.Controls;

internal static class MarkdownBlockIdentity
{
    public static string Key(MarkdownBlock block) => block.GetType().Name + JsonSerializer.Serialize(Shape(block));

    private static object Shape(MarkdownBlock block) => block switch
    {
        QuoteBlock quote => new { quote.Inlines, Blocks = quote.Blocks?.Select(Shape) },
        ListBlock list => new { list.Ordered, list.Start, Items = list.Items.Select(item => new
        {
            item.IndentLevel, item.Marker, item.Inlines, item.IsTask, item.IsChecked,
            Children = item.ChildBlocks?.Select(Shape),
        }) },
        _ => block,
    };

    public static int TableCount(MarkdownBlock block) => block switch
    {
        TableBlock => 1,
        QuoteBlock quote => quote.Blocks?.Sum(TableCount) ?? 0,
        ListBlock list => list.Items.Sum(item => item.ChildBlocks?.Sum(TableCount) ?? 0),
        _ => 0,
    };
}
