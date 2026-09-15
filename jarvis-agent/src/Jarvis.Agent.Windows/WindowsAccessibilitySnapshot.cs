using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using Jarvis.Agent.Core;
using Jarvis.Protocol;

namespace Jarvis.Agent.Windows;

/// <summary>Best-effort, bounded Windows UI Automation observation. Access failures return partial state.</summary>
public sealed class WindowsComputerObservationProvider : IComputerObservationProvider
{
    public const int MaxNodes = 200;

    public ComputerObservation Capture()
    {
        string? title = null;
        int? processId = null;
        string? processName = null;
        FocusedAutomationElement? focused = null;
        var nodes = new List<AccessibilityNode>(MaxNodes);

        var hwnd = GetForegroundWindow();
        if (hwnd != IntPtr.Zero)
        {
            try { title = ReadWindowText(hwnd); } catch { }
            try
            {
                _ = GetWindowThreadProcessId(hwnd, out var pid);
                if (pid != 0)
                {
                    processId = unchecked((int)pid);
                    using var process = Process.GetProcessById(processId.Value);
                    processName = Clip(process.ProcessName, 256);
                }
            }
            catch { }
        }

        try
        {
            var element = AutomationElement.FocusedElement;
            if (element is not null) focused = ToFocused(element);
        }
        catch { }

        if (hwnd != IntPtr.Zero)
        {
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                if (root is not null) Collect(root, nodes);
            }
            catch { }
        }

        return new ComputerObservation(Clip(title, 512), processId, Clip(processName, 256), focused, nodes);
    }

    private static void Collect(AutomationElement root, List<AccessibilityNode> nodes)
    {
        var queue = new Queue<AutomationElement>();
        queue.Enqueue(root);
        while (queue.Count > 0 && nodes.Count < MaxNodes)
        {
            var current = queue.Dequeue();
            try { nodes.Add(ToNode(current)); } catch { }
            if (nodes.Count >= MaxNodes) break;
            try
            {
                var child = TreeWalker.ControlViewWalker.GetFirstChild(current);
                while (child is not null && queue.Count + nodes.Count < MaxNodes * 2)
                {
                    queue.Enqueue(child);
                    child = TreeWalker.ControlViewWalker.GetNextSibling(child);
                }
            }
            catch { }
        }
    }

    private static AccessibilityNode ToNode(AutomationElement element)
    {
        var role = "Unknown";
        string? name = null;
        string? automationId = null;
        string? value = null;
        try { role = Clip(element.Current.ControlType?.ProgrammaticName?.Replace("ControlType.", "", StringComparison.Ordinal), 128) ?? "Unknown"; } catch { }
        try { name = Clip(element.Current.Name, 384); } catch { }
        try { automationId = Clip(element.Current.AutomationId, 256); } catch { }
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern valuePattern)
                value = Clip(valuePattern.Current.Value, 384);
        }
        catch { }
        return new AccessibilityNode(role, name, automationId, value);
    }

    private static FocusedAutomationElement ToFocused(AutomationElement element)
    {
        var node = ToNode(element);
        return new FocusedAutomationElement(node.Role, node.Name, node.AutomationId, node.Value);
    }

    private static string? ReadWindowText(IntPtr hwnd)
    {
        var length = Math.Clamp(GetWindowTextLength(hwnd), 0, 4096);
        if (length == 0) return null;
        var buffer = new StringBuilder(length + 1);
        _ = GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    internal static string? Clip(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value[..max];
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);
}

public sealed class ComputerStateTool : IAgentTool
{
    public const int MaxOutputChars = 32_000;
    private readonly ComputerStateTracker _states;
    private readonly IComputerObservationProvider _observer;

    public ComputerStateTool(ComputerStateTracker states, IComputerObservationProvider observer)
    {
        _states = states ?? throw new ArgumentNullException(nameof(states));
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
    }

    public ToolDescriptor Descriptor { get; } = new(
        "computer.get_state",
        "computer__get_state",
        "computer",
        "Observe the current Windows foreground window, focused accessible element and a bounded accessibility tree. Returns an opaque stateId that must be used for the next computer_batch. Partial UI Automation access is reported as partial state rather than escalating privileges.",
        WireJson.Element(new { type = "object", properties = new { }, additionalProperties = false }),
        ReadOnly: true,
        Sensitive: true);

    public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = _states.Capture(context.SessionId, _observer.Capture());
        var text = SerializeBounded(snapshot);
        return Task.FromResult(new ToolReply(text));
    }

    internal static string SerializeBounded(ComputerStateSnapshot snapshot)
    {
        var observation = snapshot.Observation;
        var nodes = observation.Nodes.Take(WindowsComputerObservationProvider.MaxNodes).ToArray();
        while (true)
        {
            var payload = new
            {
                stateId = snapshot.StateId,
                generation = snapshot.Generation,
                observedAt = snapshot.ObservedAt,
                observation = new
                {
                    foregroundTitle = WindowsComputerObservationProvider.Clip(observation.ForegroundTitle, 512),
                    foregroundProcessId = observation.ForegroundProcessId,
                    foregroundProcessName = WindowsComputerObservationProvider.Clip(observation.ForegroundProcessName, 256),
                    focused = observation.Focused is null ? null : new
                    {
                        role = WindowsComputerObservationProvider.Clip(observation.Focused.Role, 128),
                        name = WindowsComputerObservationProvider.Clip(observation.Focused.Name, 384),
                        automationId = WindowsComputerObservationProvider.Clip(observation.Focused.AutomationId, 256),
                        value = WindowsComputerObservationProvider.Clip(observation.Focused.Value, 384)
                    },
                    nodes = nodes.Select(node => new
                    {
                        role = WindowsComputerObservationProvider.Clip(node.Role, 128),
                        name = WindowsComputerObservationProvider.Clip(node.Name, 384),
                        automationId = WindowsComputerObservationProvider.Clip(node.AutomationId, 256),
                        value = WindowsComputerObservationProvider.Clip(node.Value, 384)
                    }).ToArray(),
                    truncated = nodes.Length < observation.Nodes.Count
                }
            };
            var json = JsonSerializer.Serialize(payload, WireJson.Options);
            if (json.Length <= MaxOutputChars || nodes.Length == 0) return json.Length <= MaxOutputChars ? json : json[..MaxOutputChars];
            nodes = nodes.Take(Math.Max(0, nodes.Length * 3 / 4)).ToArray();
        }
    }
}
