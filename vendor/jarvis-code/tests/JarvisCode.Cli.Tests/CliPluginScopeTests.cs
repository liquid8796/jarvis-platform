using System.IO.Compression;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using JarvisCode.Core.Mcp;

namespace JarvisCode.Cli.Tests;

public sealed class CliPluginScopeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-cli-plugin-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Local_plugin_loads_all_components_without_installing_or_changing_source()
    {
        var plugin = Path.Combine(_root, "alpha");
        Write(plugin, ".claude-plugin/plugin.json", "{\"name\":\"alpha\"}");
        Write(plugin, "commands/explain.md", "---\ndescription: Explain\n---\nRead ${CLAUDE_PLUGIN_ROOT}/docs.");
        Write(plugin, "agents/reviewer.md", "---\nname: reviewer\nmodel: sonnet\nmaxTurns: 3\n---\nReview code.");
        Write(plugin, "skills/check/SKILL.md", "---\nname: check\ndescription: Check code\n---\nCheck $ARGUMENTS.");
        Write(plugin, "hooks/hooks.json", "{\"hooks\":{\"SessionStart\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"echo ready\"}]}]}}");
        Write(plugin, ".mcp.json", "{\"mcpServers\":{\"docs\":{\"command\":\"node\",\"args\":[\"${CLAUDE_PLUGIN_ROOT}/server.js\"]}}}");
        var original = Directory.GetFiles(plugin, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllText);
        using var http = new HttpClient(new FixtureHttp([]));
        string stage;
        await using (var scope = await CliPluginScope.CreateAsync([plugin + Path.DirectorySeparatorChar], [], http, default))
        {
            stage = scope.Directory;
            Assert.Equal("alpha:explain", Assert.Single(scope.Content.Commands).Name);
            Assert.Contains(plugin, scope.Content.Commands[0].Template);
            Assert.Equal("alpha:reviewer", Assert.Single(scope.Content.Agents).Name);
            Assert.Equal(3, scope.Content.Agents[0].MaxTurns);
            Assert.Equal("alpha:check", Assert.Single(scope.Content.Skills).Name);
            var hook = JsonNode.Parse(File.ReadAllText(Assert.Single(scope.Content.HookFiles)))!;
            Assert.Equal(plugin, hook["$jarvis_plugin_root"]!.GetValue<string>());
            var server = Assert.Single(McpConfig.LoadSingleFile(Assert.Single(scope.Content.McpFiles)));
            Assert.Equal("plugin:alpha:docs", server.Name);
            Assert.Equal(plugin + "/server.js", Assert.Single(server.Args));
            Assert.Equal(plugin, server.Env["CLAUDE_PLUGIN_ROOT"]);
        }
        Assert.False(Directory.Exists(stage));
        foreach (var file in original) Assert.Equal(file.Value, File.ReadAllText(file.Key));
        Assert.False(File.Exists(Path.Combine(plugin, "settings.json")));
    }

    [Fact]
    public async Task Explicit_paths_replace_commands_and_agents_but_add_skills()
    {
        var plugin = Path.Combine(_root, "custom");
        Write(plugin, ".claude-plugin/plugin.json", """
            {"name":"custom","commands":"./extra/command.md","agents":"./extra/agent.md","skills":["./extra/skill"],
             "userConfig":{"endpoint":{"type":"string","default":"https://service.example"}},
             "mcpServers":{"api":{"url":"${user_config.endpoint}/mcp","type":"http"}},
             "lspServers":{"lang":{"command":"./server.exe","extensionToLanguage":{".test":"test"}}}}
            """);
        Write(plugin, "commands/ignored.md", "Ignored.");
        Write(plugin, "agents/ignored.md", "Ignored.");
        Write(plugin, "extra/command.md", "Actual command.");
        Write(plugin, "extra/agent.md", "---\nname: expert\n---\nActual agent.");
        Write(plugin, "skills/base/SKILL.md", "---\nname: base\n---\nBase.");
        Write(plugin, "extra/skill/SKILL.md", "---\nname: extra\n---\nExtra.");
        using var http = new HttpClient(new FixtureHttp([]));
        await using var scope = await CliPluginScope.CreateAsync([plugin], [], http, default);
        Assert.Equal("custom:command", Assert.Single(scope.Content.Commands).Name);
        Assert.Equal("custom:expert", Assert.Single(scope.Content.Agents).Name);
        Assert.Equal(new[] { "custom:base", "custom:extra" }, scope.Content.Skills.Select(skill => skill.Name).Order().ToArray());
        Assert.Equal("https://service.example/mcp", Assert.Single(McpConfig.LoadSingleFile(scope.Content.McpFiles[0])).Url);
        var lsp = JsonNode.Parse(File.ReadAllText(Assert.Single(scope.LspFiles)))!["plugin:custom:lang"]!;
        Assert.Equal(Path.Combine(plugin, "server.exe"), lsp["command"]!.GetValue<string>());
        Assert.Equal(plugin, lsp["env"]!["CLAUDE_PLUGIN_ROOT"]!.GetValue<string>());
    }

    [Fact]
    public async Task Url_archive_and_local_zip_are_scoped_and_root_skill_name_is_stable()
    {
        Directory.CreateDirectory(_root);
        var bytes = Archive(("./SKILL.md", "---\nname: one\ndescription: One skill\n---\nDo one thing."));
        var zip = Path.Combine(_root, "single.zip");
        File.WriteAllBytes(zip, bytes);
        using var http = new HttpClient(new FixtureHttp(bytes));
        await using var local = await CliPluginScope.CreateAsync([zip], [], http, default);
        Assert.Equal("single:one", Assert.Single(local.Content.Skills).Name);
        await using var remote = await CliPluginScope.CreateAsync([], ["https://plugins.example/single.zip"], http, default);
        Assert.Equal("single:one", Assert.Single(remote.Content.Skills).Name);
        Assert.StartsWith(remote.Directory, remote.Content.Installed[0].Directory);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("C:/escape.txt")]
    [InlineData("NUL.txt")]
    public async Task Unsafe_archive_paths_are_rejected_before_loading(string entry)
    {
        Directory.CreateDirectory(_root);
        var zip = Path.Combine(_root, "bad.zip");
        File.WriteAllBytes(zip, Archive((entry, "untrusted")));
        using var http = new HttpClient(new FixtureHttp([]));
        await Assert.ThrowsAsync<InvalidDataException>(() => CliPluginScope.CreateAsync([zip], [], http, default));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
    }

    [Fact]
    public async Task Explicit_component_cannot_escape_plugin_root()
    {
        var plugin = Path.Combine(_root, "bad");
        Write(plugin, ".claude-plugin/plugin.json", "{\"name\":\"bad\",\"commands\":\"./../outside.md\"}");
        using var http = new HttpClient(new FixtureHttp([]));
        await Assert.ThrowsAsync<InvalidDataException>(() => CliPluginScope.CreateAsync([plugin], [], http, default));
    }

    private static void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static byte[] Archive(params (string Path, string Text)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(entry.Path).Open());
                writer.Write(entry.Text);
            }
        return stream.ToArray();
    }

    private sealed class FixtureHttp(byte[] zip) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip), RequestMessage = request });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
