using System.IO;
using System.Text;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// The mermaid render page carries reference prose the C# tokenizer cannot see:
/// <c>Assets/Mermaid/host.html</c> is JavaScript, and
/// <see cref="PortedTextParityTests"/> reads C# sources. Every sentence in it
/// that this port took from the reference's own module — its extraction
/// refusals, its timeout message, its CSP — is therefore checked here instead,
/// against the same desktop corpus.
///
/// The vendored library beside it is checked too. It is mermaid 11.16.1 from
/// npm, and the reference ships the same build, so the two files being byte for
/// byte identical is what says this copy is the one the reference renders with —
/// while the copy itself came from the package, not from that install.
/// </summary>
public class MermaidHostPageParityTests
{
    /// <summary>
    /// The sentences the page shares with the reference's <c>zl</c> and
    /// <c>Bl</c> (ion-dist chunk <c>shared-12-4ZL7iq1e.js</c>). The timeout's
    /// number is interpolated in both builds, so only its two halves are pinned.
    /// </summary>
    private static readonly string[] Sentences =
    [
        "mermaid sandbox render: missing data-uri iframe src",
        "mermaid sandbox render: no <svg> in iframe body",
        "mermaid sandbox render: CSP not injected via host.appendChild",
        "mermaid render exceeded ",
        "default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:",
        "position:fixed;top:0;left:0;width:100vw;height:100vh;visibility:hidden;pointer-events:none",
    ];

    private static string HostPagePath => Path.Combine(
        RepoPaths.Root, "src", "JarvisCode.App", "Assets", "Mermaid", "host.html");

    private static string MermaidPath => Path.Combine(
        RepoPaths.Root, "src", "JarvisCode.App", "Assets", "Mermaid", "mermaid.min.js");

    [Fact]
    public void The_render_page_carries_the_sentences_it_says_it_carries()
    {
        var page = File.ReadAllText(HostPagePath);
        var missing = Sentences.Where(s => !page.Contains(s, StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0,
            "Assets/Mermaid/host.html no longer carries:\n  " + string.Join("\n  ", missing));
    }

    [ReferenceAppFact]
    public void Every_one_of_them_is_still_the_references_own()
    {
        var desktop = ReferenceCorpora.Desktop;
        var drifted = Sentences.Where(s => !desktop.Contains(s)).ToList();
        Assert.True(drifted.Count == 0,
            new StringBuilder()
                .AppendLine("Assets/Mermaid/host.html says something the installed desktop app "
                            + (ReferenceInstall.AppVersion ?? "unknown") + " does not:")
                .AppendLine(string.Join("\n", drifted.Select(static s => "  " + s)))
                .ToString());
    }

    [ReferenceAppFact]
    public void The_vendored_build_is_the_one_the_reference_renders_with()
    {
        var shipped = Path.Combine(
            ReferenceInstall.AppDirectory!, "resources", "ion-dist", "_frame-rt", "_runtime",
            "mermaid-11.16.1.min.js");
        Assert.True(File.Exists(shipped), "the reference no longer ships mermaid-11.16.1.min.js at " + shipped);
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(shipped))),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(MermaidPath))));
    }
}
