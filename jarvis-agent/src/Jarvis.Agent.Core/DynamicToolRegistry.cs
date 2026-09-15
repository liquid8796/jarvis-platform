using System.Security.Cryptography;
using System.Text.Json;
using Jarvis.Protocol;
using Json.Schema;

namespace Jarvis.Agent.Core;

/// <summary>
/// Runtime source of truth for installed tools. Catalog changes are explicit and
/// immutable snapshots keep each invocation internally consistent.
/// </summary>
public sealed class DynamicToolRegistry
{
    private readonly object _sync = new();
    private DynamicToolSnapshot _snapshot;

    public DynamicToolRegistry(IEnumerable<IAgentTool> tools)
    {
        _snapshot = BuildSnapshot(1, tools);
    }

    public DynamicToolSnapshot Snapshot
    {
        get { lock (_sync) return _snapshot; }
    }

    public event Action<DynamicToolSnapshot>? Changed;

    public DynamicToolSnapshot Replace(IEnumerable<IAgentTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        DynamicToolSnapshot? changed = null;
        lock (_sync)
        {
            var candidate = BuildSnapshot(_snapshot.Generation + 1, tools);
            var sameImplementations = candidate.Tools.Count == _snapshot.Tools.Count &&
                candidate.Tools.All(pair => _snapshot.Tools.TryGetValue(pair.Key, out var current) && ReferenceEquals(current, pair.Value));
            if (sameImplementations && StringComparer.Ordinal.Equals(candidate.Digest, _snapshot.Digest))
                return _snapshot;
            _snapshot = candidate;
            changed = candidate;
        }
        Changed?.Invoke(changed);
        return changed;
    }

    public static string CreateDigest(IEnumerable<ToolDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var ordered = descriptors.OrderBy(d => d.Id, StringComparer.Ordinal).Select(d => new
        {
            d.Id,
            d.Name,
            d.Category,
            d.Description,
            InputSchema = d.InputSchema,
            d.ReadOnly,
            d.Sensitive
        }).ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ordered, WireJson.Options);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static DynamicToolSnapshot BuildSnapshot(long generation, IEnumerable<IAgentTool> tools)
    {
        var map = new Dictionary<string, IAgentTool>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            var id = tool.Descriptor.Id;
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Tool ID cannot be empty.", nameof(tools));
            if (!map.TryAdd(id, tool)) throw new ArgumentException("Duplicate tool ID: " + id, nameof(tools));
        }

        var schemas = map.ToDictionary(pair => pair.Key, pair => SchemaGuard.Compile(pair.Value.Descriptor.InputSchema), StringComparer.Ordinal);
        var descriptors = map.Values.Select(t => t.Descriptor).OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();
        return new DynamicToolSnapshot(generation, CreateDigest(descriptors), map, schemas, descriptors);
    }
}

public sealed record DynamicToolSnapshot(
    long Generation,
    string Digest,
    IReadOnlyDictionary<string, IAgentTool> Tools,
    IReadOnlyDictionary<string, JsonSchema> Schemas,
    IReadOnlyList<ToolDescriptor> Descriptors);
