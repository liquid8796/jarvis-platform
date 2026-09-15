using System.IO;
using System.Text.Json;
using System.Windows.Documents;
using JarvisCode.App.Controls;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

[Collection("Native window tests")]
public sealed class ComposerDocumentTests
{
    [Fact]
    public void DraftsKeepRichIdentityAndArgumentsPerSessionAcrossStoreReload()
    {
        var file = Path.Combine(Path.GetTempPath(), "jarvis-composer-tests", Guid.NewGuid().ToString("N"), "drafts.json");
        var first = new ComposerDocument([new(Skill: new("plugin:first", "First", "description", "<file>")), new("\"a b\""),
            new(Skill: new("second", "Second")), new(" --flag")], Caret: 4);
        var second = new ComposerDocument([new("Different draft")]);
        var store = new ComposerDraftStore(file);
        store.Save("code:one", first);
        store.Save("code:two", second);
        var restored = new ComposerDraftStore(file);
        Assert.Equal("/plugin:first \"a b\"/second --flag", restored.Get("code:one").Text);
        Assert.Equal("<file>", restored.Get("code:one").Nodes[0].Skill!.ArgumentHint);
        Assert.Equal(4, restored.Get("code:one").Caret);
        Assert.Equal("Different draft", restored.Get("code:two").Text);
        Assert.Equal("/first /second args", new ComposerDocument([new(Skill: new("first", "First")), new(Skill: new("second", "Second")), new("args")]).Text);
    }

    [NativeUiFact]
    public void RichSkillAtomsSupportSelectionDeleteUndoCopyAndHistoryWithoutChangingSerializedCommands()
    {
        WpfTestThread.Run(() =>
        {
            var chip = new ComposerSkillChip("plugin:simplify", "simplify", "Simplifies code", "<file>");
            var editor = new ComposerBox { FontSize = 14, ResolveSkill = id => id == chip.SkillId ? chip : null };
            var window = new System.Windows.Window { Content = editor, Width = 400, Height = 160, Left = -10000, Top = -10000, ShowActivated = false };
            window.Show();
            window.UpdateLayout();
            editor.Text = "/sim";
            editor.InsertSkill(chip, 0, 4);
            Assert.Equal("/plugin:simplify ", editor.Text);
            Assert.Equal("<file>", editor.ArgumentHint);
            Assert.IsType<InlineUIContainer>(((Paragraph)editor.Document.Blocks.FirstBlock!).Inlines.FirstInline);
            editor.CaretIndex = editor.Text.Length;
            editor.ReplaceSelection(new ComposerDocument([new("\"a b\" --flag")]));
            Assert.Equal("/plugin:simplify \"a b\" --flag", editor.Text);
            Assert.Empty(editor.ArgumentHint);
            var draft = JsonSerializer.Deserialize<ComposerDocument>(JsonSerializer.Serialize(editor.Snapshot()))!;
            editor.Clear();
            editor.Restore(draft);
            Assert.Equal(draft.Text, editor.Text);
            editor.Select(2, 2); // a partial textual range still selects the complete atom
            Assert.Equal("/plugin:simplify", editor.SelectedText);
            var copy = editor.CopySelection();
            Assert.Equal("/plugin:simplify", copy.GetData(System.Windows.DataFormats.UnicodeText));
            var copied = JsonSerializer.Deserialize<ComposerDocument>((string)copy.GetData(ComposerBox.ClipboardDocumentFormat))!;
            Assert.Equal(chip, copied.Nodes[0].Skill);
            editor.ReplaceSelection(ComposerDocument.Empty);
            Assert.DoesNotContain("simplify", editor.Text, StringComparison.Ordinal);
            Assert.True(editor.CanUndo, "native undo history was empty after deleting the atom");
            editor.Undo();
            Assert.True(editor.Text.Contains("/plugin:simplify", StringComparison.Ordinal), "after undo: " + editor.Text + "; nodes: " +
                string.Join(",", ((Paragraph)editor.Document.Blocks.FirstBlock!).Inlines.Select(inline => inline.GetType().Name + ":" + inline.Tag)));
            editor.Text = "/plugin:simplify argument"; // history is still plain text
            Assert.IsType<InlineUIContainer>(((Paragraph)editor.Document.Blocks.FirstBlock!).Inlines.FirstInline);
                editor.CaretIndex = "/plugin:simplify".Length;
                Assert.True(editor.SelectPreviousSkill());
                Assert.Equal("/plugin:simplify", editor.SelectedText);
                editor.Select("/plugin:simplify".Length, 1);
                editor.ReplaceSelection(ComposerDocument.Empty);
                Assert.Equal("/plugin:simplify argument", editor.Text); // separator remains in serialization
                Assert.True(editor.SelectPreviousSkill());
                Assert.Equal("/plugin:simplify", editor.SelectedText);
                editor.Undo();
                editor.CaretIndex = editor.Text.Length;
            editor.ReplaceSelection(new ComposerDocument([new(" /sec")]));
            var query = editor.SlashQuery()!.Value;
            Assert.Equal("sec", query.Query);
            editor.InsertSkill(new("second", "second"), query.Start, query.Length);
            Assert.Equal("/plugin:simplify argument /second ", editor.Text);
            window.Close();
        });
    }

    [NativeUiFact]
    public void TheRealComposerXamlHostsAtomsAndKeepsTheHintOutOfTheMessage()
    {
        WpfTestThread.Run(() =>
        {
            var surface = new JarvisCode.App.Views.ChatSurface();
            var window = new System.Windows.Window { Content = surface, Width = 900, Height = 650,
                Left = -10000, Top = -10000, ShowActivated = false };
            window.Show();
            window.UpdateLayout();
            var editor = Assert.IsType<ComposerBox>(surface.FindName("InputBox"));
            editor.InsertSkill(new("verify", "verify", "Verify changes", "<test target>"), 0, 0);
            window.UpdateLayout();
            Assert.Equal("/verify ", surface.ComposerText);
            Assert.Equal("<test target>", editor.ArgumentHint);
            Assert.Single(((Paragraph)editor.Document.Blocks.FirstBlock!).Inlines.OfType<InlineUIContainer>());
            Assert.True(editor.GetRectFromCharacterIndex(editor.Text.Length).Left > editor.GetRectFromCharacterIndex(0).Left);
            window.Close();
        });
    }

    [NativeUiFact]
    public void RepeatedTextReadsReuseTheSerializedDocumentCacheAndEditsKeepItCurrent()
    {
        WpfTestThread.Run(() =>
        {
            var editor = new ComposerBox { FontSize = 14 };
            var window = new System.Windows.Window { Content = editor, Width = 500, Height = 180,
                Left = -10000, Top = -10000, ShowActivated = false };
            try
            {
                window.Show();
                window.UpdateLayout();
                var text = string.Concat(Enumerable.Repeat("A long prompt line that must not be reserialized on every read. ", 512));
                editor.Text = text;
                editor.CaretIndex = text.Length;

                var cached = editor.Text;
                _ = editor.Text.Length; // Warm up the dependency-property getter before measuring it.
                var before = GC.GetAllocatedBytesForCurrentThread();
                var observedLength = 0;
                for (var index = 0; index < 1_000; index++) observedLength += editor.Text.Length;
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                Assert.Same(cached, editor.Text);
                Assert.Same(editor.GetValue(ComposerBox.TextProperty), editor.Text);
                Assert.Equal(text.Length * 1_000, observedLength);
                Assert.True(allocated < 16_384, $"Reading cached composer text allocated {allocated:N0} bytes.");

                var textSeenByChangedHandler = "";
                editor.TextChanged += (_, _) => textSeenByChangedHandler = editor.Text;
                editor.ReplaceSelection(new ComposerDocument([new("!")]));
                Assert.Equal(text + "!", editor.Text);
                Assert.Equal(editor.Text, textSeenByChangedHandler);
                Assert.Equal(editor.Text, editor.Snapshot().Text);
                editor.Undo();
                Assert.Equal(text, editor.Text);
                Assert.Equal(editor.Text, textSeenByChangedHandler);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
