using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Microsoft.Win32;

namespace Jarvis.Agent.Windows;

public static class BrowserIntegration
{
    public const string ExtensionPipeName = "JarvisAgent-browser-extension-v2";
    public const string ServicePipeName = "JarvisAgent-browser-service-v1";
    public const string HostName = "com.jarvis.agent.browser";
    public const string ExtensionId = "kaofhfhpenfnaekhbeikeapgchmfcnjj";
    public const string BrowserHostExeName = "jarvis-browser-host.exe";
    public const string BrowserServiceExeName = "jarvis-browser-service.exe";

    // Kept only so older callers compiled against PipeName keep targeting the new extension transport.
    public const string PipeName = ExtensionPipeName;

    public static string Install(string nativeHostExe)
    {
        if (!File.Exists(nativeHostExe) ||
            !Path.GetFileName(nativeHostExe).Equals(BrowserHostExeName, StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException($"Select the published {BrowserHostExeName} native-messaging host.");

        var target = Path.Combine(AgentProfile.Root, "browser-extension");
        Directory.CreateDirectory(target);
        var source = Path.Combine(AppContext.BaseDirectory, "Assets", "Browser");
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException("Browser extension assets are missing from this Agent package.");

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }

        // Keep the host manifest outside the unpacked extension directory.
        var manifest = Path.Combine(AgentProfile.Root, "browser-host.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            name = HostName,
            description = "Jarvis Agent dedicated browser native host",
            path = Path.GetFullPath(nativeHostExe),
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{ExtensionId}/" }
        }, new JsonSerializerOptions { WriteIndented = true }));

        foreach (var browser in new[] { @"Software\Google\Chrome", @"Software\Microsoft\Edge", @"Software\CocCoc\Browser" })
        {
            using var key = Registry.CurrentUser.CreateSubKey(browser + @"\NativeMessagingHosts\" + HostName);
            key.SetValue(null, manifest);
        }
        return target;
    }

    public static string ResolveCompanionExecutable(string fileName)
    {
        var direct = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(direct)) return direct;
        var browser = Path.Combine(AppContext.BaseDirectory, "browser", fileName);
        if (File.Exists(browser)) return browser;
        throw new FileNotFoundException($"Browser companion '{fileName}' is missing from this Agent package.");
    }

    /// <summary>
    /// The browser service can inherit a high-integrity token when the Agent itself is run as
    /// Administrator, while Chrome deliberately starts native-messaging hosts at medium integrity.
    /// Keep the DACL restricted to this Windows user, but label the extension pipe medium so that
    /// the same user's native host is not rejected by mandatory integrity control.
    /// </summary>
    internal static NamedPipeServerStream CreateExtensionPipeServer(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (!OperatingSystem.IsWindows())
            return new NamedPipeServerStream(pipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var sddl = CreateExtensionPipeSecurityDescriptor();
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptor,
                InheritHandle = false
            };
            var handle = CreateNamedPipe(@"\\.\pipe\" + pipeName,
                PipeAccessDuplex | FileFlagOverlapped,
                PipeRejectRemoteClients,
                byte.MaxValue, 0, 0, 0, ref attributes);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { LocalFree(descriptor); }
    }

    internal static string CreateExtensionPipeSecurityDescriptor()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var user = identity.User ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        return $"O:{user.Value}G:{user.Value}D:P(A;;GA;;;SY)(A;;GA;;;{user.Value})S:(ML;;NW;;;ME)";
    }

    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeRejectRemoteClients = 0x00000008;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor, uint stringSdRevision, out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode,
        uint maxInstances, uint outBufferSize, uint inBufferSize, uint defaultTimeout,
        ref SecurityAttributes securityAttributes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    // Backward compatibility for users whose pre-1.0.71 manifest still points at jarvis-agent.exe.
    public static bool IsNativeHostInvocation(string[] args) =>
        args.Any(a => a.StartsWith("chrome-extension://" + ExtensionId + "/", StringComparison.Ordinal));
}
