using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text.Json.Nodes;
using JarvisCode.Core.Ide;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>The editor connection used by both desktop and CLI harnesses.</summary>
public static class IdeServices
{
    private static readonly ConcurrentDictionary<string, string> Selected = new(StringComparer.OrdinalIgnoreCase);
    public static IdeBridge Bridge { get; } = new();
    static IdeServices() => AppDomain.CurrentDomain.ProcessExit += (_, _) => Bridge.Dispose();
    public static IReadOnlyList<ITool> Tools(string workingDirectory) =>
        Bridge.Discover(workingDirectory).Count == 0 ? [] : [new DiagnosticsTool(workingDirectory)];

    public static Task<JsonNode> CallAsync(string workingDirectory, string method, JsonObject arguments,
        CancellationToken cancellationToken = default) => Bridge.CallAsync(workingDirectory, method, arguments,
            cancellationToken, Selected.GetValueOrDefault(Path.GetFullPath(workingDirectory)));

    public static async Task<string> AutoConnectAsync(string workingDirectory, CancellationToken token = default)
    {
        var editors = Bridge.Discover(workingDirectory);
        if (editors.Count == 0) return "No editor is available for this folder.";
        if (editors.Count != 1) return "Several editors have this folder open. Use /ide use <id> to choose one.";
        Selected[Path.GetFullPath(workingDirectory)] = editors[0].Id;
        await Bridge.ConnectAsync(workingDirectory, token, editors[0].Id);
        return "Connected to " + editors[0].Name + ".";
    }

    public static async Task AddSelectionAsync(JarvisCode.Core.Sessions.Session session, CancellationToken token)
    {
        if (session.Messages.LastOrDefault() is not { Role: JarvisCode.Core.Models.Role.User, IsMeta: false } message ||
            message.Content.OfType<JarvisCode.Core.Models.TextBlock>().Any(t => t.Text.StartsWith("<ide_selection ", StringComparison.Ordinal))) return;
        if (await SelectionContextAsync(session.WorkingDirectory, token) is { } selection)
            session.Messages[^1] = SystemReminders.AddUserBlocks(message, [new JarvisCode.Core.Models.TextBlock(selection)]);
    }

    public static async Task<string?> SelectionContextAsync(string workingDirectory, CancellationToken token)
    {
        if (Bridge.Discover(workingDirectory).Count == 0) return null;
        try
        {
            var selection = await CallAsync(workingDirectory, "getSelection", new JsonObject(), token);
            if (selection["text"]?.ToString() is not { Length: > 0 } text) return null;
            return $"<ide_selection file=\"{SecurityElement.Escape(selection["filePath"]?.ToString() ?? "")}\">\n" +
                   SecurityElement.Escape(text.Length > 64000 ? text[..64000] : text) + "\n</ide_selection>";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            if (token.IsCancellationRequested) throw;
            return null;
        }
    }

    public static async Task<string> CommandAsync(string workingDirectory, string arguments, CancellationToken token = default)
    {
        var command = arguments.Trim();
        if (command.Equals("auto", StringComparison.OrdinalIgnoreCase)) return await AutoConnectAsync(workingDirectory, token);
        if (command.Equals("install", StringComparison.OrdinalIgnoreCase)) return await IdeExtensionInstaller.InstallAsync(token);
        if (command.StartsWith("use ", StringComparison.OrdinalIgnoreCase))
        {
            var id = command[4..].Trim();
            var matches = Bridge.Discover(workingDirectory).Where(e => e.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) return "Choose an unambiguous editor id from /ide.";
            Selected[Path.GetFullPath(workingDirectory)] = matches[0].Id;
            return "Connected to " + matches[0].Name + ".";
        }
        if (command == "selection") return (await CallAsync(workingDirectory, "getSelection", new JsonObject(), token)).ToJsonString();
        if (command == "diagnostics") return (await CallAsync(workingDirectory, "getDiagnostics", new JsonObject(), token)).ToJsonString();
        if (command == "close-diffs") return (await CallAsync(workingDirectory, "closeAllDiffTabs", new JsonObject(), token)).ToJsonString();
        var editors = Bridge.Discover(workingDirectory);
        return editors.Count == 0
            ? "No editor is connected for this folder. Run /ide install, then open the folder in VS Code."
            : "Connected editors:\n" + string.Join('\n', editors.Select(e => $"  {e.Id[..8]} · {e.Name} · process {e.ProcessId}")) +
              "\n/ide use <id> · /ide selection · /ide diagnostics · /ide close-diffs";
    }

    private sealed class DiagnosticsTool(string workingDirectory) : ITool
    {
        public string Name => "mcp__ide__getDiagnostics";
        public string Description => "Get current language diagnostics from the connected editor, optionally for one file URI. Uses the editor's live language services.";
        public JsonObject InputSchema => new()
        {
            ["type"] = "object", ["properties"] = new JsonObject
            { ["uri"] = new JsonObject { ["type"] = "string", ["description"] = "Optional file:// URI for one workspace file." } },
            ["additionalProperties"] = false,
        };
        public bool IsReadOnly => true;
        public string DescribeCall(JsonObject arguments) => "Editor diagnostics";
        public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            try { return ToolResult.Success(context.Truncate((await CallAsync(workingDirectory, "getDiagnostics", arguments, cancellationToken)).ToJsonString(), "editor diagnostics")); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested) throw;
                return ToolResult.Error("Editor diagnostics failed: " + ex.Message);
            }
        }
    }
}

public static class IdeExtensionInstaller
{
    public static async Task<string> InstallAsync(CancellationToken token)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code");
        var executable = Path.Combine(root, "Code.exe");
        if (!File.Exists(executable)) return "VS Code was not found. Install VS Code, then run /ide install again.";
        var direct = Path.Combine(root, "resources", "app", "out", "cli.js");
        var cli = File.Exists(direct) ? direct : Directory.EnumerateDirectories(root)
            .Select(d => Path.Combine(d, "resources", "app", "out", "cli.js")).FirstOrDefault(File.Exists);
        if (cli is null) return "The VS Code command-line entry point could not be found.";
        var source = Path.Combine(AppContext.BaseDirectory, "Assets", "IdeExtension");
        if (!File.Exists(Path.Combine(source, "package.json"))) return "The IDE bridge is missing from this app installation.";
        var vsix = Path.Combine(Path.GetTempPath(), "jarvis-code-ide-" + Guid.NewGuid().ToString("N") + ".vsix");
        try
        {
            CreatePackage(source, vsix);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.Environment["ELECTRON_RUN_AS_NODE"] = "1";
            start.Environment.Remove("VSCODE_DEV");
            start.ArgumentList.Add(cli); start.ArgumentList.Add("--install-extension"); start.ArgumentList.Add(vsix);
            start.ArgumentList.Add("--do-not-sync");
            using var process = Process.Start(start) ?? throw new IOException("VS Code could not start its installer.");
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            try { await process.WaitForExitAsync(token); }
            catch { try { process.Kill(true); } catch (InvalidOperationException) { } throw; }
            var output = (await stdout) + (await stderr);
            return process.ExitCode == 0 ? "Jarvis Code IDE bridge installed. Open or reload the workspace in VS Code to connect."
                : "The IDE bridge could not be installed: " + output.Trim();
        }
        finally { if (File.Exists(vsix)) File.Delete(vsix); }
    }

    public static void CreatePackage(string sourceDirectory, string destination)
    {
        using var zip = ZipFile.Open(destination, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            zip.CreateEntryFromFile(file, "extension/" + Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/'));
        Write("[Content_Types].xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="json" ContentType="application/json"/><Default Extension="js" ContentType="application/javascript"/><Default Extension="md" ContentType="text/markdown"/><Default Extension="vsixmanifest" ContentType="text/xml"/></Types>
            """);
        Write("extension.vsixmanifest", """
            <?xml version="1.0" encoding="utf-8"?>
            <PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011"><Metadata><Identity Language="en-US" Id="jarvis-code-ide" Version="1.0.0" Publisher="jarvis-code"/><DisplayName>Jarvis Code IDE Bridge</DisplayName><Description xml:space="preserve">Connect Jarvis Code to editor diagnostics, selection and diffs.</Description><Categories>Other</Categories><Properties><Property Id="Microsoft.VisualStudio.Code.Engine" Value="^1.96.0"/><Property Id="Microsoft.VisualStudio.Code.ExtensionDependencies" Value=""/></Properties></Metadata><Installation><InstallationTarget Id="Microsoft.VisualStudio.Code"/></Installation><Dependencies/><Assets><Asset Type="Microsoft.VisualStudio.Code.Manifest" Path="extension/package.json" Addressable="true"/></Assets></PackageManifest>
            """);
        void Write(string name, string text)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new System.Text.UTF8Encoding(false));
            writer.Write(text);
        }
    }
}
