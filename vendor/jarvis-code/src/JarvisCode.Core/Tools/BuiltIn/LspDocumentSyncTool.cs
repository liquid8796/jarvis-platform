using System.Text.Json.Nodes;
using JarvisCode.Core.LanguageServers;

namespace JarvisCode.Core.Tools.BuiltIn;

/// <summary>Keeps explicitly configured language servers current after successful file tools.</summary>
public sealed class LspDocumentSyncTool(ITool inner, PluginLanguageServerManager manager) : ITool
{
    public string Name => inner.Name;
    public string Description => inner.Description;
    public JsonObject InputSchema => inner.InputSchema;
    public bool IsReadOnly => inner.IsReadOnly;
    public string DescribeCall(JsonObject arguments) => inner.DescribeCall(arguments);

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var result = await inner.ExecuteAsync(arguments, context, cancellationToken);
        if (result.IsError || JsonArgs.GetString(arguments, "file_path") is not { Length: > 0 } filePath) return result;
        try
        {
            var diagnostics = await manager.SynchronizeDocumentAsync(context.ResolvePath(filePath), inner.Name is "Write" or "Edit", cancellationToken);
            if (!string.IsNullOrWhiteSpace(diagnostics))
                result = result with { Content = result.Content + "\n\nLSP diagnostics:\n" + context.Truncate(diagnostics, "LSP diagnostics") };
        }
        catch (OperationCanceledException) { cancellationToken.ThrowIfCancellationRequested(); }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        { result = result with { Content = result.Content + "\n\nLSP synchronization failed: " + error.Message }; }
        return result;
    }
}
