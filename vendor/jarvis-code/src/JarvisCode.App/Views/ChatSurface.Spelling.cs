using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace JarvisCode.App.Views;

/// <summary>
/// The composer's spell check and its context menu, ported from the reference's own
/// web-contents menu (app.asar <c>index.chunk-DnlgCaT3.js</c>, its <c>zV</c>): the
/// dictionary's suggestions first, each replacing the word, then "Add to
/// dictionary" when a word is misspelled, then the edit rows — Undo and Redo only
/// while there is something to undo, then Cut, Copy, Paste and Select All.
/// </summary>
public partial class ChatSurface
{
    private Services.CustomDictionary? _dictionary;

    /// <summary>
    /// Turns spell checking on for the composer and points it at the profile's own
    /// added-word list. The reference's dictionary is Chromium's, which keeps its
    /// custom words per profile too.
    /// </summary>
    private void InitializeSpelling(string profileRoot)
    {
        _dictionary = new Services.CustomDictionary(profileRoot);
        SpellCheck.SetIsEnabled(InputBox, true);
        ReloadDictionary();
        // The box needs a menu of its own for ContextMenuOpening to be raised at
        // all; the handler replaces its rows with the ones the click earned.
        InputBox.ContextMenu = new ContextMenu();
        InputBox.ContextMenuOpening += OnComposerContextMenuOpening;
    }

    private void ReloadDictionary()
    {
        if (_dictionary is null)
        {
            return;
        }

        // The collection is re-read when it changes, so the added word takes effect
        // without restarting the app.
        SpellCheck.GetCustomDictionaries(InputBox).Clear();
        SpellCheck.GetCustomDictionaries(InputBox).Add(_dictionary.Uri);
    }

    private void OnComposerContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = new ContextMenu();
        var index = InputBox.CaretIndex;
        var error = InputBox.GetSpellingError(index);
        var wrote = false;

        void Separator()
        {
            if (wrote && menu.Items.Count > 0)
            {
                menu.Items.Add(new Separator());
                wrote = false;
            }
        }

        if (error is not null)
        {
            foreach (var suggestion in error.Suggestions)
            {
                var replacement = suggestion;
                var item = new MenuItem { Header = replacement };
                item.Click += (_, _) => error.Correct(replacement);
                menu.Items.Add(item);
                wrote = true;
            }

            Separator();
            var word = MisspelledWord(index);
            var add = new MenuItem { Header = AddToDictionaryLabel };
            add.Click += (_, _) =>
            {
                if (word.Length > 0 && _dictionary?.Add(word) == true)
                {
                    ReloadDictionary();
                }
            };
            menu.Items.Add(add);
            wrote = true;
        }

        Separator();
        var canUndo = InputBox.CanUndo;
        var canRedo = InputBox.CanRedo;
        if (canUndo || canRedo)
        {
            menu.Items.Add(Command("Undo", ApplicationCommands.Undo, canUndo));
            menu.Items.Add(Command("Redo", ApplicationCommands.Redo, canRedo));
            wrote = true;
            Separator();
        }

        var hasSelection = InputBox.SelectionLength > 0;
        menu.Items.Add(Command("Cut", ApplicationCommands.Cut, hasSelection));
        menu.Items.Add(Command("Copy", ApplicationCommands.Copy, hasSelection));
        menu.Items.Add(Command("Paste", ApplicationCommands.Paste, Clipboard.ContainsText()));
        menu.Items.Add(Command("Select All", ApplicationCommands.SelectAll, InputBox.Text.Length > 0));

        InputBox.ContextMenu = menu;
    }

    /// <summary>The reference's own label for adding a word the checker flagged.</summary>
    public const string AddToDictionaryLabel = "Add to dictionary";

    private MenuItem Command(string header, RoutedUICommand command, bool enabled)
    {
        var item = new MenuItem
        {
            Header = header,
            Command = command,
            CommandTarget = InputBox,
            IsEnabled = enabled,
        };
        return item;
    }

    /// <summary>The word the caret sits in, which is what "Add to dictionary" adds.</summary>
    private string MisspelledWord(int caretIndex)
    {
        var text = InputBox.Text;
        if (text.Length == 0)
        {
            return "";
        }

        var start = Math.Min(caretIndex, text.Length - 1);
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
        {
            start--;
        }

        var end = Math.Min(caretIndex, text.Length);
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            end++;
        }

        return text[start..end].Trim();
    }
}
