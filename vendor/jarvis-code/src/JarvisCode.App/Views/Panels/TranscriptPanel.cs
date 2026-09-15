using System.Windows;
using System.Windows.Controls;
using JarvisCode.App.Services;
using JarvisCode.App.ViewModels;

namespace JarvisCode.App.Views.Panels;

/// <summary>
/// The Transcript pane, ported from the reference's HH (ion-dist chunk
/// c360a9e1c-DUoNQd2W.js, its transcript case): this session's own message feed in a pane,
/// with "No messages yet" when it has none. The rows are the transcript's real items, so
/// the pane carries the surface's resources and lets the ordinary template walk find them.
/// </summary>
public sealed class TranscriptPanel : UserControl
{
    private readonly ContentControl _host = new() { Focusable = false };
    private readonly ItemsControl _items = new() { Focusable = false, Margin = new Thickness(12, 4, 12, 24) };
    private bool _resourcesSet;

    public TranscriptPanel()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(PanePrimitives.HeaderHeight) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(PanePrimitives.Title(SidePanes.Title(SidePanes.Transcript)));

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = _host,
        };
        Grid.SetRow(scroller, 1);
        grid.Children.Add(scroller);
        Content = grid;
    }

    /// <summary>
    /// Hands the pane the transcript's own resource dictionary once, so its rows resolve
    /// the surface's DataTemplates. Assigning a dictionary registers the element as one of
    /// its owners, so this happens exactly once — the same rule the subagent view follows.
    /// </summary>
    public void UseTemplatesFrom(ResourceDictionary resources)
    {
        if (_resourcesSet)
        {
            return;
        }

        _resourcesSet = true;
        _items.Resources = resources;
    }

    public void Load(ChatViewModel viewModel)
    {
        _items.ItemsSource = viewModel.Transcript;
        _host.Content = viewModel.Transcript.Count == 0
            ? PanePrimitives.EmptyState("FileGlyph", "No messages yet", "")
            : _items;
        viewModel.Transcript.CollectionChanged -= OnTranscriptChanged;
        viewModel.Transcript.CollectionChanged += OnTranscriptChanged;
    }

    private void OnTranscriptChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (sender is System.Collections.ICollection { Count: > 0 } && !ReferenceEquals(_host.Content, _items))
        {
            _host.Content = _items;
        }
    }
}
