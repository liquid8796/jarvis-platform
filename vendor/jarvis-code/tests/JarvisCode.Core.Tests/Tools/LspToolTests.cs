using System.Text.Json.Nodes;
using JarvisCode.Core.LanguageServers;
using JarvisCode.Core.Permissions;
using JarvisCode.Core.Security;
using JarvisCode.Core.Tools;
using JarvisCode.Core.Tools.BuiltIn;

namespace JarvisCode.Core.Tests.Tools;

/// <summary>Real local stdio subprocess; no user language servers, editors, credentials or model calls.</summary>
public sealed class LspToolTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _script;
    private readonly string _source;
    private readonly string _log;

    public LspToolTests()
    {
        _script = Path.Combine(_temp.Path, "mock-language-server.cjs");
        _source = Path.Combine(_temp.Path, "source.mock");
        _log = Path.Combine(_temp.Path, "messages.jsonl");
        File.WriteAllText(_script, ServerScript);
        File.WriteAllText(_source, "function café() { return 'ภาษาไทย'; }\r\n");
    }

    private LanguageServerConfiguration Configuration(params (string Key, string Value)[] environment) => new()
    {
        Name = "plugin:test:mock", Command = "node", Arguments = [_script],
        ExtensionToLanguage = new Dictionary<string, string> { [".mock"] = "mock" },
        Environment = environment.Append((Key: "MOCK_LOG", Value: _log)).ToDictionary(pair => pair.Key, pair => pair.Value),
        Settings = JsonNode.Parse("""{"mock":{"enabled":true}}"""),
        InitializationOptions = JsonNode.Parse("""{"test":"ภาษาไทย"}"""),
        StartupTimeout = 5000, ShutdownTimeout = 500,
    };

    private PluginLanguageServerManager Manager(params (string Key, string Value)[] environment) =>
        new(new[] { Configuration(environment) }, _temp.Path);

    private Task<JsonNode?> Call(PluginLanguageServerManager manager, string operation, int line = 1, CancellationToken token = default) =>
        manager.ExecuteAsync(operation, _source, line, 2, "café", token);

    private JsonObject[] Messages() => File.Exists(_log)
        ? File.ReadAllLines(_log).Select(line => JsonNode.Parse(line)!.AsObject()).ToArray() : [];

    [Fact]
    public async Task EveryReferenceOperationExecutesOverStdioAndFormatsMeaningfulResults()
    {
        await using var manager = Manager();
        var tool = new LspTool(manager);
        foreach (var operation in new[] { "goToDefinition", "findReferences", "hover", "documentSymbol", "workspaceSymbol", "goToImplementation", "prepareCallHierarchy", "incomingCalls", "outgoingCalls" })
        {
            var result = await tool.ExecuteAsync(new JsonObject
            { ["operation"] = operation, ["filePath"] = _source, ["line"] = 1, ["character"] = 2, ["query"] = "café" },
                new ToolExecutionContext { WorkingDirectory = _temp.Path }, default);
            Assert.False(result.IsError, result.Content);
            Assert.Contains(operation == "hover" ? "ภาษาไทย" : operation.Contains("Definition") || operation.Contains("Implementation") || operation == "findReferences" ? "source.mock:3:4" : "café", result.Content);
        }
        var messages = Messages();
        var initialize = Assert.Single(messages, message => message["method"]?.ToString() == "initialize");
        Assert.Equal(PluginLanguageServerManager.FileUri(_temp.Path), initialize["params"]!["rootUri"]!.ToString());
        Assert.Equal("ภาษาไทย", initialize["params"]!["initializationOptions"]!["test"]!.ToString());
        Assert.Single(messages, message => message["method"]?.ToString() == "textDocument/didOpen");
        Assert.Contains(messages, message => message["id"]?.ToString() == "server-config" && message["result"]?[0]?["enabled"]?.GetValue<bool>() == true);
        var reference = Assert.Single(messages, message => message["method"]?.ToString() == "textDocument/references");
        Assert.True(reference["params"]!["context"]!["includeDeclaration"]!.GetValue<bool>());
        Assert.Equal(0, reference["params"]!["position"]!["line"]!.GetValue<int>());
        Assert.Equal(1, reference["params"]!["position"]!["character"]!.GetValue<int>());
        Assert.Contains(messages, message => message["method"]?.ToString() == "callHierarchy/incomingCalls" && message["params"]?["item"]?["data"]?["token"]?.ToString() == "opaque");
    }

    [Fact]
    public async Task SynchronizesIncrementalDocumentsAndGracefullyClosesBeforeExit()
    {
        var manager = Manager();
        await Call(manager, "hover");
        await File.WriteAllTextAsync(_source, "function café() { return 'updated 🚀'; }\r\nnext");
        var hover = await Call(manager, "hover");
        Assert.Contains("updated 🚀", hover!["contents"]!["value"]!.ToString());
        await manager.DisposeAsync();
        var messages = Messages();
        var change = Assert.Single(messages, message => message["method"]?.ToString() == "textDocument/didChange");
        Assert.Equal(2, change["params"]!["textDocument"]!["version"]!.GetValue<int>());
        Assert.Equal(1, change["params"]!["contentChanges"]![0]!["range"]!["end"]!["line"]!.GetValue<int>());
        Assert.Equal(0, change["params"]!["contentChanges"]![0]!["range"]!["end"]!["character"]!.GetValue<int>());
        Assert.Contains(messages, message => message["method"]?.ToString() == "textDocument/didSave");
        var methods = messages.Select(message => message["method"]?.ToString()).Where(method => method is not null).ToList();
        Assert.True(methods.IndexOf("textDocument/didClose") < methods.IndexOf("shutdown"));
        Assert.True(methods.IndexOf("shutdown") < methods.IndexOf("exit"));
        Assert.Equal("exit", methods[^1]);
    }

    [Fact]
    public async Task CancellationSendsCancelRequestAndLeavesServerUsable()
    {
        await using var manager = Manager();
        await Call(manager, "hover");
        using var cancellation = new CancellationTokenSource(150);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call(manager, "hover", 98, cancellation.Token));
        Assert.NotNull(await Call(manager, "hover"));
        Assert.Contains(Messages(), message => message["method"]?.ToString() == "$/cancelRequest");
    }

    [Fact]
    public async Task RestartsCrashedProcessAndReopensDocumentsWithinConfiguredBudget()
    {
        await using var manager = new PluginLanguageServerManager(new[] { Configuration() with { MaxRestarts = 1 } }, _temp.Path);
        await Call(manager, "hover");
        await Assert.ThrowsAsync<IOException>(() => Call(manager, "hover", 99));
        Assert.NotNull(await Call(manager, "hover"));
        Assert.Equal(2, Messages().Count(message => message["method"]?.ToString() == "initialize"));
        await Assert.ThrowsAsync<IOException>(() => Call(manager, "hover", 99));
        var error = await Assert.ThrowsAsync<IOException>(() => Call(manager, "hover"));
        Assert.Contains("restart limit", error.Message);
    }

    [Fact]
    public async Task UnsupportedOperationsAndFileTypesAreErrorsNotEmptySuccesses()
    {
        await using var manager = Manager(("MOCK_NO_DEFINITION", "1"));
        var unsupported = await Assert.ThrowsAsync<InvalidOperationException>(() => Call(manager, "goToDefinition"));
        Assert.Contains("does not support", unsupported.Message);
        var foreign = Path.Combine(_temp.Path, "source.other");
        File.WriteAllText(foreign, "hello");
        var noServer = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ExecuteAsync("hover", foreign, 1, 1, null, default));
        Assert.Contains("No LSP server", noServer.Message);
        Assert.DoesNotContain(Messages(), message => message["method"]?.ToString() == "textDocument/definition");
    }

    [Fact]
    public async Task StartupTimeoutAndMalformedFramesFailAndDisposeWithoutHanging()
    {
        await using (var manager = new PluginLanguageServerManager(new[] { Configuration(("MOCK_HANG_INIT", "1")) with { StartupTimeout = 250, ShutdownTimeout = 100 } }, _temp.Path))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call(manager, "hover").WaitAsync(TimeSpan.FromSeconds(5)));
        await using (var manager = Manager(("MOCK_BAD_FRAME", "1")))
            await Assert.ThrowsAsync<IOException>(() => Call(manager, "hover").WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ModelSeesServerErrorsAndValidationWithoutLaunchingUnconfiguredProcesses()
    {
        await using var manager = Manager();
        var tool = new LspTool(manager);
        var context = new ToolExecutionContext { WorkingDirectory = _temp.Path };
        var args = new JsonObject { ["operation"] = "hover", ["filePath"] = _source, ["line"] = 0, ["character"] = 1 };
        Assert.True((await tool.ExecuteAsync(args, context, default)).IsError);
        Assert.Empty(Messages());
        args["line"] = 100;
        var result = await tool.ExecuteAsync(args, context, default);
        Assert.True(result.IsError);
        Assert.Contains("controlled failure", result.Content);
    }

    [Fact]
    public async Task ConfigurationAcceptsSocketAsStdioAliasAndRejectsEmptyExtensionMappings()
    {
        var file = Path.Combine(_temp.Path, ".lsp.json");
        File.WriteAllText(file, """{"mock":{"command":"node","extensionToLanguage":{},"transport":"stdio"}}""");
        Assert.Throws<InvalidDataException>(() => LanguageServerConfiguration.Load([file]));
        File.WriteAllText(file, """{"mock":{"command":"node","extensionToLanguage":{".mock":"mock"},"transport":"socket"}}""");
        var socket = LanguageServerConfiguration.Load([file]);
        await using var manager = new PluginLanguageServerManager(new[] { socket[0] with { Arguments = [_script], Environment = Configuration().Environment } }, _temp.Path);
        Assert.NotNull(await Call(manager, "hover"));
    }

    [Fact]
    public void ReadOnlyNavigationStillHonorsFileScopeAndDeferral()
    {
        var scope = WorkspacePathScope.FromRoot(_temp.Path);
        Assert.Equal(CallRisk.AutoAllowable, ToolCallPolicy.Assess("LSP", true, new JsonObject { ["filePath"] = _source }, scope, _temp.Path).Risk);
        Assert.Equal(CallRisk.Escalated, ToolCallPolicy.Assess("LSP", true, new JsonObject { ["filePath"] = Path.Combine(Path.GetTempPath(), "outside.mock") }, scope, _temp.Path).Risk);
        Assert.True(ToolDeferral.IsDeferredBuiltIn("LSP"));
    }

    [Fact]
    public async Task EditsPublishDiagnosticsAndDiagnosticsFalsePreservesNavigation()
    {
        await using var manager = Manager();
        var tool = new LspDocumentSyncTool(new WriteFileTool(), manager);
        var result = await tool.ExecuteAsync(new JsonObject { ["file_path"] = _source, ["content"] = "invalid source" },
            new ToolExecutionContext { WorkingDirectory = _temp.Path }, default);
        Assert.False(result.IsError);
        Assert.Contains("LSP diagnostics:", result.Content);
        Assert.Contains("mock type error", result.Content);
        await using var disabled = new PluginLanguageServerManager(new[] { Configuration() with { Diagnostics = false } }, _temp.Path);
        Assert.Null(await disabled.SynchronizeDocumentAsync(_source, true, default));
        Assert.NotNull(await Call(disabled, "hover"));
    }

    [Fact]
    public async Task InvalidConfigurationDoesNotClaimExtensionsFromLaterValidServer()
    {
        var config = Path.Combine(_temp.Path, ".lsp.json");
        File.WriteAllText(config, new JsonObject
        {
            ["bad"] = new JsonObject { ["extensionToLanguage"] = new JsonObject { [".mock"] = "mock" } },
            ["good"] = new JsonObject { ["command"] = "node", ["args"] = new JsonArray(_script), ["env"] = new JsonObject { ["MOCK_LOG"] = _log }, ["extensionToLanguage"] = new JsonObject { [".mock"] = "mock" } },
        }.ToJsonString());
        await using var manager = new PluginLanguageServerManager(new[] { config }, _temp.Path);
        Assert.Single(manager.ConfigurationWarnings);
        Assert.NotNull(await Call(manager, "hover"));
    }

    [Fact]
    public async Task WindowsCmdLanguageServerShimSupportsPathsWithSpaces()
    {
        if (!OperatingSystem.IsWindows()) return;
        var launcher = Path.Combine(_temp.Path, "server launcher.cmd");
        var scriptWithSpaces = Path.Combine(_temp.Path, "mock server.cjs");
        File.Copy(_script, scriptWithSpaces);
        File.WriteAllText(launcher, "@echo off\r\nnode %*\r\n");
        await using var manager = new PluginLanguageServerManager(new[] { Configuration() with
        { Command = launcher, Arguments = [scriptWithSpaces] } }, _temp.Path);
        Assert.NotNull(await Call(manager, "hover"));
    }

    public void Dispose() => _temp.Dispose();

    private const string ServerScript = """
        const fs = require('node:fs');
        let bytes = Buffer.alloc(0), uri, text = '';
        const range = {start:{line:2,character:3},end:{line:2,character:7}};
        const log = m => fs.appendFileSync(process.env.MOCK_LOG, JSON.stringify(m) + '\n');
        function send(message) {
          const body = Buffer.from(JSON.stringify(message), 'utf8');
          const header = Buffer.from('Content-Length: ' + body.length + '\r\nContent-Type: application/vscode-jsonrpc; charset=utf-8\r\n\r\n');
          process.stdout.write(header.subarray(0,7)); process.stdout.write(header.subarray(7));
          process.stdout.write(body.subarray(0,11)); process.stdout.write(body.subarray(11));
        }
        const symbol = () => ({name:'café',kind:12,uri,range,selectionRange:range,data:{token:'opaque'}});
        function handle(m) {
          log(m);
          if (!m.method) return;
          if (m.method === 'exit') process.exit(0);
          if (m.method === 'textDocument/didOpen') {uri=m.params.textDocument.uri; text=m.params.textDocument.text;}
          if (m.method === 'textDocument/didChange') text=m.params.contentChanges[0].text;
          if (m.method === 'textDocument/didOpen' || m.method === 'textDocument/didChange')
            send({jsonrpc:'2.0',method:'textDocument/publishDiagnostics',params:{uri,version:m.params.textDocument.version,diagnostics:[{range,severity:1,message:'mock type error'}]}});
          if (m.id === undefined) return;
          if (m.method === 'initialize' && process.env.MOCK_HANG_INIT) return;
          if (m.method === 'initialize' && process.env.MOCK_BAD_FRAME) {process.stdout.write('Content-Length: nope\r\n\r\n'); return;}
          if (m.params?.position?.line === 97) return;
          if (m.params?.position?.line === 98) {process.stderr.write('controlled crash'); process.exit(41);}
          if (m.params?.position?.line === 99) {send({jsonrpc:'2.0',id:m.id,error:{code:-32001,message:'controlled failure'}}); return;}
          let result = null;
          switch(m.method) {
            case 'initialize':
              send({jsonrpc:'2.0',id:'server-config',method:'workspace/configuration',params:{items:[{section:'mock'}]}});
              result={capabilities:{positionEncoding:'utf-16',textDocumentSync:{openClose:true,change:2,save:{includeText:true}},definitionProvider:!process.env.MOCK_NO_DEFINITION,referencesProvider:true,hoverProvider:true,documentSymbolProvider:true,workspaceSymbolProvider:true,implementationProvider:true,callHierarchyProvider:true}}; break;
            case 'textDocument/definition': case 'textDocument/implementation': result=[{targetUri:uri,targetRange:range,targetSelectionRange:range}]; break;
            case 'textDocument/references': result=[{uri,range}]; break;
            case 'textDocument/hover': result={contents:{kind:'markdown',value:text}}; break;
            case 'textDocument/documentSymbol': result=[{name:'café',kind:12,range,selectionRange:range,children:[{name:'nested',kind:13,range,selectionRange:range}]}]; break;
            case 'workspace/symbol': result=[{name:m.params.query,kind:12,location:{uri,range}}]; break;
            case 'textDocument/prepareCallHierarchy': result=[symbol()]; break;
            case 'callHierarchy/incomingCalls': result=[{from:symbol(),fromRanges:[range]}]; break;
            case 'callHierarchy/outgoingCalls': result=[{to:symbol(),fromRanges:[range]}]; break;
          }
          send({jsonrpc:'2.0',id:m.id,result});
        }
        process.stdin.on('data', data => {
          bytes=Buffer.concat([bytes,data]);
          while(true) {
            const end=bytes.indexOf('\r\n\r\n'); if(end<0) return;
            const length=Number(/Content-Length: (\d+)/i.exec(bytes.subarray(0,end).toString())[1]);
            if(bytes.length<end+4+length) return;
            const message=JSON.parse(bytes.subarray(end+4,end+4+length).toString('utf8'));
            bytes=bytes.subarray(end+4+length); handle(message);
          }
        });
        """;
}
