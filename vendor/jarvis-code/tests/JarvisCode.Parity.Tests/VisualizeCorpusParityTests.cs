using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// The <c>visualize</c> server's corpus, checked against the reference's own
/// answers rather than against a second copy of the text.
///
/// <c>Captures/Mcp/gen-visualize-corpus.js</c> runs the reference's
/// <c>getImagineServerDef</c> in a vm, calls <c>read_me</c> once per platform
/// and once per platform-and-module, and records a sha256 and a character count
/// for all 24 — plus the empty call, which is base and footer alone. Reproducing
/// those hashes is the whole proof that this port composes what the reference
/// composes: the sections, their order, the deduplication and the separator all
/// have to be right for one hash to land.
///
/// Nothing here needs the reference installed; the recording is the reference.
/// </summary>
public sealed class VisualizeCorpusParityTests
{
    private static readonly JsonObject Fixture = Load();

    private static JsonObject Load()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "Captures", "Mcp",
            $"visualize-corpus-{McpReferenceFixture.Build}.json");
        Assert.True(File.Exists(path), $"The visualize recording is missing: {path}");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    private static string Sha256(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string[] Modules => [.. Fixture["modules"]!.AsArray().Select(n => (string)n!)];

    private static string[] Platforms => [.. Fixture["platforms"]!.AsArray().Select(n => (string)n!)];

    private static void AssertSample(string key, string answer)
    {
        var sample = Fixture["samples"]![key]!.AsObject();
        Assert.True(
            (int)sample["chars"]! == answer.Length && Sha256(answer) == (string)sample["sha256"]!,
            $"read_me sample \"{key}\": this build answers {answer.Length} chars " +
            $"({Sha256(answer)}), the reference answered {(int)sample["chars"]!} chars " +
            $"({(string)sample["sha256"]!}).");
    }

    [Fact]
    public void Read_me_reproduces_every_recorded_answer()
    {
        foreach (var platform in Platforms)
        {
            AssertSample(platform, VisualizeCorpus.ReadMe(Modules, platform));
            foreach (var module in Modules)
            {
                AssertSample($"{platform}/{module}", VisualizeCorpus.ReadMe([module], platform));
            }
        }
    }

    [Fact]
    public void Read_me_with_no_arguments_is_base_and_footer()
    {
        var empty = Fixture["readMeEmpty"]!.AsObject();
        var answer = VisualizeCorpus.ReadMe(null, null);
        Assert.Equal((int)empty["chars"]!, answer.Length);
        Assert.Equal((string)empty["sha256"]!, Sha256(answer));
    }

    [Fact]
    public void Every_embedded_section_matches_the_recording()
    {
        foreach (var (file, node) in Fixture["assets"]!.AsObject())
        {
            var meta = node!.AsObject();
            var text = file == "widget-runtime.html"
                ? VisualizeCorpus.WidgetRuntimeHtml
                : VisualizeCorpus.Section(file[..^".md".Length]);
            Assert.True(
                (int)meta["chars"]! == text.Length && Sha256(text) == (string)meta["sha256"]!,
                $"Assets/Visualize/{file}: this build embeds {text.Length} chars " +
                $"({Sha256(text)}), the recording holds {(int)meta["chars"]!} " +
                $"({(string)meta["sha256"]!}).");
        }
    }

    [Fact]
    public void The_widths_and_the_composition_are_the_recorded_ones()
    {
        foreach (var (platform, node) in Fixture["widths"]!.AsObject())
        {
            Assert.Equal((int)node!, VisualizeCorpus.WidthFor(platform));
        }

        foreach (var (width, byModule) in Fixture["composition"]!.AsObject())
        {
            foreach (var (module, sections) in byModule!.AsObject())
            {
                Assert.Equal(
                    sections!.AsArray().Select(n => (string)n!).ToArray(),
                    VisualizeCorpus.SectionsFor(int.Parse(width), module).ToArray());
            }
        }
    }

    [Fact]
    public void The_separator_and_the_module_and_platform_lists_are_the_recorded_ones()
    {
        Assert.Equal(VisualizeCorpus.Separator, (string)Fixture["separator"]!);
        Assert.Equal(Modules, VisualizeCorpus.Modules.ToArray());
        Assert.Equal(Platforms, VisualizeCorpus.Platforms.ToArray());
    }

    [Fact]
    public void The_resource_and_its_runtime_are_the_recorded_ones()
    {
        var resource = Fixture["resources"]!.AsArray()[0]!.AsObject();
        Assert.Equal(VisualizeTools.RuntimeUri, (string)resource["uri"]!);
        Assert.Equal(VisualizeTools.RuntimeName, (string)resource["name"]!);
        Assert.Equal(VisualizeTools.RuntimeDescription, (string)resource["description"]!);
        Assert.Equal(VisualizeTools.RuntimeMimeType, (string)resource["mimeType"]!);

        var runtime = Fixture["runtime"]!.AsObject();
        Assert.Equal((string)runtime["sha256"]!, Sha256(VisualizeCorpus.WidgetRuntimeHtml));

        var csp = runtime["meta"]!["ui"]!["csp"]!.AsObject();
        Assert.Equal(
            csp["connectDomains"]!.AsArray().Select(n => (string)n!).ToArray(),
            VisualizeTools.RuntimeSecurity.ConnectDomains.ToArray());
        Assert.Equal(
            csp["resourceDomains"]!.AsArray().Select(n => (string)n!).ToArray(),
            VisualizeTools.RuntimeSecurity.ResourceDomains.ToArray());
        Assert.True(VisualizeTools.RuntimeSecurity.ClipboardWrite);
    }

    [Fact]
    public void Show_widget_answers_the_recorded_sentence()
    {
        Assert.Equal(VisualizeTools.ShowWidgetResult, (string)Fixture["showWidgetResult"]!);
    }

    [Fact]
    public void The_server_serves_its_runtime_through_the_shell()
    {
        InternalMcpServerDefinition[] servers = [VisualizeTools.Server()];
        var context = new InternalMcpSessionContext();

        var listed = InternalMcpServers.ListResources(servers, context);
        var only = Assert.Single(listed);
        Assert.Equal(InternalMcpServerNames.Visualize, only.ServerName);
        Assert.Equal(VisualizeTools.RuntimeUri, only.Resource.Uri);

        var contents = InternalMcpServers.ReadResource(servers, context, VisualizeTools.RuntimeUri);
        Assert.NotNull(contents);
        Assert.Equal(VisualizeTools.RuntimeMimeType, contents!.MimeType);
        Assert.Equal(VisualizeCorpus.WidgetRuntimeHtml, contents.Text);
        Assert.Equal(
            (string)Fixture["runtime"]!["meta"]!.ToJsonString(),
            contents.Meta!.ToJsonString());

        Assert.Null(InternalMcpServers.ReadResource(servers, context, "ui://imagine/nope.html"));

        // A Chat-surface session is not the reference's ccd, and the server goes
        // with its resources.
        var chat = new InternalMcpSessionContext { SessionType = "chat" };
        Assert.Empty(InternalMcpServers.ListResources(servers, chat));
        Assert.Null(InternalMcpServers.ReadResource(servers, chat, VisualizeTools.RuntimeUri));
    }
}
