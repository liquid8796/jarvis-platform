using System.IO;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// The manifest half of the UI-string check: every string this app renders that
/// the reference also ships, asserted in both directions — the reference still
/// says it under that message id, and we still render it.
///
/// Both directions matter. Without the first, a reference rewording goes
/// unnoticed and the port quietly speaks a version nobody ships. Without the
/// second, a string we deleted stays "covered" by a row about nothing.
///
/// The rows hold what *this* app renders, so a string this app rebranded is
/// stored as "Jarvis" and compared through <see cref="UiBrand"/>; a reference
/// rewording still fails, because both candidates are derived from what the
/// reference says now.
/// </summary>
public sealed class UiStringManifestTests
{
    public static TheoryData<string, string, string> Entries
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            foreach (var entry in UiStringManifest.Load())
            {
                data.Add(entry.MessageId, entry.Source, entry.Text);
            }

            // A theory with no cases fails the run, and an empty manifest is a
            // real failure — it means the file was lost, not that all is well.
            if (data.Count == 0)
            {
                data.Add("(none)", "(none)", "(the manifest is empty or missing)");
            }

            return data;
        }
    }

    [ReferenceAppTheory]
    [MemberData(nameof(Entries))]
    public void Manifest_string_still_matches_the_reference(string messageId, string source, string text)
    {
        Assert.True(messageId != "(none)",
            $"Manifest/ui-strings.tsv is missing or empty ({UiStringManifest.FilePath}); " +
            "regenerate it with JARVIS_APPROVE_UI_STRINGS=1.");

        var catalogue = ReferenceInstall.Catalogue!;
        Assert.True(catalogue.TryGetValue(messageId, out var reference),
            $"the reference desktop app {ReferenceInstall.AppVersion} no longer has the message id " +
            $"'{messageId}' ({Preview(text)}), which {source} renders. Re-find the string by its wording " +
            "and re-approve the manifest.");
        Assert.True(UiBrand.Matches(reference!, text),
            $"'{messageId}' now reads {Preview(reference!)} in the reference desktop app " +
            $"{ReferenceInstall.AppVersion}, while {source} still renders {Preview(text)} — which is " +
            $"neither that text nor its rebranding {Preview(UiBrand.Apply(reference!))}.");
    }

    [ReferenceAppTheory]
    [MemberData(nameof(Entries))]
    public void Manifest_string_is_still_rendered_by_its_source(string messageId, string source, string text)
    {
        if (messageId == "(none)")
        {
            return; // Already reported by the assertion above.
        }

        Assert.True(RepoPaths.SourceExists(source),
            $"{source} is gone but Manifest/ui-strings.tsv still credits it with {Preview(text)}.");
        Assert.True(RepoPaths.ReadSource(source).Contains(EscapeForSource(text), StringComparison.Ordinal),
            $"{source} no longer contains {Preview(text)} — either it moved (re-approve the manifest) " +
            "or the UI stopped saying what the reference says.");
    }

    /// <summary>
    /// The completeness guard: a reference string this app started rendering
    /// after the manifest was written would otherwise be covered by nothing.
    /// </summary>
    [ReferenceAppFact]
    public void Every_reference_string_the_app_renders_is_in_the_manifest()
    {
        var manifest = UiStringManifest.Load().Select(static e => e.Text).ToHashSet(StringComparer.Ordinal);
        var scanned = UiStringManifest.Scan(ReferenceInstall.Catalogue!);
        var unpinned = scanned.Where(entry => !manifest.Contains(entry.Text)).ToList();

        Assert.True(unpinned.Count == 0,
            $"{unpinned.Count} string(s) this app renders are in the reference catalogue but not in " +
            "Manifest/ui-strings.tsv, so nothing would notice the reference rewording them:\n  " +
            string.Join("\n  ", unpinned.Take(15).Select(e => $"{e.MessageId}  {e.Source}  {Preview(e.Text)}")) +
            "\nRe-approve with JARVIS_APPROVE_UI_STRINGS=1.");
    }

    /// <summary>
    /// Text is stored decoded, and a source file may spell it with escapes; the
    /// common case (a newline in a XAML attribute or a C# literal) is handled so
    /// the "is it still rendered" check does not fail on spelling.
    /// </summary>
    private static string EscapeForSource(string text) =>
        text.Contains('\n') ? text.Split('\n')[0] : text;

    private static string Preview(string text)
    {
        var single = text.ReplaceLineEndings("\\n");
        return single.Length > 90 ? $"\"{single[..90]}…\"" : $"\"{single}\"";
    }
}

/// <summary>Rewrites the manifest from the installed desktop app.</summary>
public sealed class UiStringManifestApproval
{
    [ApprovalFact("JARVIS_APPROVE_UI_STRINGS")]
    public void Rewrite_manifest()
    {
        Assert.True(ReferenceInstall.Catalogue is not null, ReferenceInstall.CatalogueMissingReason);
        var entries = UiStringManifest.Scan(ReferenceInstall.Catalogue!);
        UiStringManifest.Write(UiStringManifest.SourcePath, entries, ReferenceInstall.AppVersion ?? "?");
        Assert.Fail($"wrote {entries.Count} strings to {UiStringManifest.SourcePath}. Review the diff, " +
                    "then re-run without the variable. This run proved nothing.");
    }
}
