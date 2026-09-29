using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Execution;
using Jarvis.Protocol;

namespace Jarvis.Agent.Windows;

/// <summary>
/// Read-only native diagnostics. This surface intentionally has no process-memory reads,
/// debugger attach, thread-context mutation, injection, token duplication or protection bypass.
/// </summary>
public sealed class NativeInspectionToolSet
{
    private readonly string _privateRoot;
    public IReadOnlyList<IAgentTool> Tools { get; }

    public NativeInspectionToolSet(string? privateRoot = null)
    {
        _privateRoot = ToolExecutionResources.CanonicalPath(privateRoot ?? AgentProfile.Root);
        Tools =
        [
            new Tool("binary_inspect", RejectPrivate),
            new Tool("process_list"),
            new Tool("process_inspect")
        ];
    }

    private void RejectPrivate(string path)
    {
        var target = ToolExecutionResources.CanonicalPath(path);
        if (target.Equals(_privateRoot, StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith(_privateRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The agent's private profile cannot be inspected by binary diagnostics.");
    }

    private sealed class Tool(string operation, Action<string>? rejectPrivate = null) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = Describe(operation);

        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return Task.FromResult(operation switch
                {
                    "binary_inspect" => InspectBinary(arguments, context, cancellationToken, rejectPrivate),
                    "process_list" => ListProcesses(arguments),
                    _ => InspectProcess(arguments)
                });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or
                                       UnauthorizedAccessException or Win32Exception or CryptographicException or
                                       BadImageFormatException or PlatformNotSupportedException)
            {
                return Task.FromResult(ToolReply.Error(ex.Message));
            }
        }
    }

    private static ToolDescriptor Describe(string operation) => new(
        "diagnostics." + operation,
        operation,
        "diagnostics",
        operation switch
        {
            "binary_inspect" => "Read bounded metadata from a local PE/binary file: hashes, PE headers and sections, version/signing metadata, and bounded printable string samples. This never executes the file or disassembles/decrypts protected content.",
            "process_list" => "List a bounded set of running processes with basic identity and accessibility metadata. No process memory is read.",
            _ => "Inspect one running process using query-only Windows APIs: path, start time, architecture, elevation/integrity and an optional bounded module list. No debugger attach, memory read/write, injection, token duplication, SYSTEM transition or protected-process/anti-cheat bypass is attempted."
        },
        operation switch
        {
            "binary_inspect" => BinarySchema(),
            "process_list" => ProcessListSchema(),
            _ => ProcessInspectSchema()
        },
        ReadOnly: true,
        Sensitive: true);

    private static JsonElement BinarySchema() => WireJson.Element(new
    {
        type = "object",
        properties = new Dictionary<string, object>
        {
            ["file_path"] = new { type = "string", minLength = 1, maxLength = 32768 },
            ["max_strings"] = new { type = "integer", minimum = 0, maximum = 500, @default = 100 },
            ["minimum_string_length"] = new { type = "integer", minimum = 4, maximum = 64, @default = 6 },
            ["max_scan_bytes"] = new { type = "integer", minimum = 4096, maximum = 67108864, @default = 16777216 }
        },
        required = new[] { "file_path" },
        additionalProperties = false
    });

    private static JsonElement ProcessListSchema() => WireJson.Element(new
    {
        type = "object",
        properties = new Dictionary<string, object>
        {
            ["name"] = new { type = "string", minLength = 1, maxLength = 260 },
            ["limit"] = new { type = "integer", minimum = 1, maximum = 500, @default = 100 }
        },
        additionalProperties = false
    });

    private static JsonElement ProcessInspectSchema() => WireJson.Element(new
    {
        type = "object",
        properties = new Dictionary<string, object>
        {
            ["process_id"] = new { type = "integer", minimum = 1 },
            ["include_modules"] = new { type = "boolean", @default = false },
            ["max_modules"] = new { type = "integer", minimum = 1, maximum = 500, @default = 100 }
        },
        required = new[] { "process_id" },
        additionalProperties = false
    });

    private static ToolReply InspectBinary(JsonElement arguments, AgentExecutionContext context, CancellationToken ct,
        Action<string>? rejectPrivate)
    {
        var raw = arguments.GetProperty("file_path").GetString() ?? "";
        var path = Path.GetFullPath(WorkspaceDirectories.ResolvePath(raw, context.Workspace));
        rejectPrivate?.Invoke(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Binary file was not found.", path);
        var maxStrings = arguments.TryGetProperty("max_strings", out var maxNode) ? maxNode.GetInt32() : 100;
        var minimumLength = arguments.TryGetProperty("minimum_string_length", out var minimumNode)
            ? minimumNode.GetInt32() : 6;
        var maxScanBytes = arguments.TryGetProperty("max_scan_bytes", out var scanNode)
            ? scanNode.GetInt32() : 16 * 1024 * 1024;

        var info = new FileInfo(path);
        string sha256;
        using (var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            sha256 = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();

        object? pe = null;
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new PEReader(stream);
            var headers = reader.PEHeaders;
            if (headers.PEHeader is not null)
            {
                pe = new
                {
                    machine = headers.CoffHeader.Machine.ToString(),
                    characteristics = headers.CoffHeader.Characteristics.ToString(),
                    timestamp_utc = DateTimeOffset.FromUnixTimeSeconds(headers.CoffHeader.TimeDateStamp),
                    subsystem = headers.PEHeader.Subsystem.ToString(),
                    magic = headers.PEHeader.Magic.ToString(),
                    image_base = headers.PEHeader.ImageBase,
                    entry_point_rva = headers.PEHeader.AddressOfEntryPoint,
                    image_size = headers.PEHeader.SizeOfImage,
                    dll_characteristics = headers.PEHeader.DllCharacteristics.ToString(),
                    sections = headers.SectionHeaders.Select(section => new
                    {
                        name = section.Name,
                        virtual_address = section.VirtualAddress,
                        virtual_size = section.VirtualSize,
                        raw_size = section.SizeOfRawData,
                        characteristics = section.SectionCharacteristics.ToString()
                    }).ToArray()
                };
            }
        }
        catch (BadImageFormatException) { }

        object? signature = null;
        try
        {
#pragma warning disable SYSLIB0057 // Authenticode extraction still requires the OS signed-file loader.
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            signature = new
            {
                subject = certificate.Subject,
                issuer = certificate.Issuer,
                thumbprint = certificate.Thumbprint?.ToLowerInvariant(),
                not_before = certificate.NotBefore.ToUniversalTime(),
                not_after = certificate.NotAfter.ToUniversalTime()
            };
        }
        catch (CryptographicException) { }

        var version = FileVersionInfo.GetVersionInfo(path);
        var strings = maxStrings == 0 ? Array.Empty<string>() :
            ExtractStrings(path, maxScanBytes, minimumLength, maxStrings, ct);
        return new ToolReply(JsonSerializer.Serialize(new
        {
            file_path = path,
            size = info.Length,
            last_write_utc = info.LastWriteTimeUtc,
            sha256,
            version = new
            {
                version.FileDescription,
                version.CompanyName,
                version.ProductName,
                version.FileVersion,
                version.ProductVersion,
                version.OriginalFilename
            },
            authenticode_certificate = signature,
            pe,
            scanned_bytes = Math.Min(info.Length, maxScanBytes),
            strings
        }, WireJson.Options));
    }

    private static string[] ExtractStrings(string path, int maxBytes, int minimumLength, int maximum, CancellationToken ct)
    {
        using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = (int)Math.Min(input.Length, maxBytes);
        var bytes = new byte[length];
        input.ReadExactly(bytes);
        var result = new List<string>(maximum);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string text)
        {
            if (text.Length < minimumLength || result.Count >= maximum || !seen.Add(text)) return;
            result.Add(text.Length <= 512 ? text : text[..512]);
        }

        var ascii = new StringBuilder();
        for (var index = 0; index < bytes.Length && result.Count < maximum; index++)
        {
            if ((index & 0xffff) == 0) ct.ThrowIfCancellationRequested();
            var value = bytes[index];
            if (value is >= 32 and <= 126) ascii.Append((char)value);
            else { Add(ascii.ToString()); ascii.Clear(); }
        }
        Add(ascii.ToString());

        for (var offset = 0; offset < 2 && result.Count < maximum; offset++)
        {
            var unicode = new StringBuilder();
            for (var index = offset; index + 1 < bytes.Length && result.Count < maximum; index += 2)
            {
                if ((index & 0xffff) == 0) ct.ThrowIfCancellationRequested();
                var value = (char)(bytes[index] | bytes[index + 1] << 8);
                if (value is >= ' ' and <= '~') unicode.Append(value);
                else { Add(unicode.ToString()); unicode.Clear(); }
            }
            Add(unicode.ToString());
        }
        return result.ToArray();
    }

    private static ToolReply ListProcesses(JsonElement arguments)
    {
        var name = arguments.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;
        var limit = arguments.TryGetProperty("limit", out var limitNode) ? limitNode.GetInt32() : 100;
        var items = new List<object>();
        foreach (var process in Process.GetProcesses().OrderBy(process => process.ProcessName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(process => process.Id))
        {
            using (process)
            {
                if (!string.IsNullOrWhiteSpace(name) &&
                    !process.ProcessName.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;
                string? path = null;
                string access = "queryable";
                try { path = process.MainModule?.FileName; }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                { access = "limited"; }
                items.Add(new
                {
                    process_id = process.Id,
                    name = process.ProcessName,
                    executable_path = path,
                    access
                });
                if (items.Count >= limit) break;
            }
        }
        return new ToolReply(JsonSerializer.Serialize(new { processes = items, truncated = items.Count >= limit }, WireJson.Options));
    }

    private static ToolReply InspectProcess(JsonElement arguments)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Process inspection currently requires Windows.");
        var pid = arguments.GetProperty("process_id").GetInt32();
        var includeModules = arguments.TryGetProperty("include_modules", out var modulesNode) && modulesNode.GetBoolean();
        var maxModules = arguments.TryGetProperty("max_modules", out var maxNode) ? maxNode.GetInt32() : 100;
        using var process = Process.GetProcessById(pid);
        var native = QueryNativeProcess(pid);
        string? path = null;
        DateTimeOffset? started = null;
        int? sessionId = null;
        string access = "queryable";
        try { path = process.MainModule?.FileName; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        { access = "limited"; }
        try { started = process.StartTime.ToUniversalTime(); } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
        try { sessionId = process.SessionId; } catch (InvalidOperationException) { }

        var modules = new List<object>();
        string? moduleError = null;
        if (includeModules)
        {
            try
            {
                foreach (ProcessModule module in process.Modules)
                {
                    using (module)
                    {
                        modules.Add(new
                        {
                            module.ModuleName,
                            module.FileName,
                            base_address = module.BaseAddress.ToInt64(),
                            module.ModuleMemorySize
                        });
                    }
                    if (modules.Count >= maxModules) break;
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            { moduleError = ex.GetType().Name; }
        }

        return new ToolReply(JsonSerializer.Serialize(new
        {
            process_id = pid,
            name = process.ProcessName,
            executable_path = path,
            start_time_utc = started,
            session_id = sessionId,
            access,
            architecture = native.Architecture,
            elevated = native.Elevated,
            integrity_level = native.IntegrityLevel,
            query_error = native.Error,
            modules,
            modules_truncated = modules.Count >= maxModules,
            module_error = moduleError
        }, WireJson.Options));
    }

    private static NativeProcessInfo QueryNativeProcess(int pid)
    {
        var process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process == IntPtr.Zero) return new(null, null, null, "OpenProcess:" + Marshal.GetLastWin32Error());
        try
        {
            string? architecture = null;
            try
            {
                if (IsWow64Process2(process, out var processMachine, out var nativeMachine))
                    architecture = MachineName(processMachine == 0 ? nativeMachine : processMachine);
            }
            catch (EntryPointNotFoundException) { }

            bool? elevated = null;
            string? integrity = null;
            if (OpenProcessToken(process, 0x0008, out var token)) // TOKEN_QUERY
            {
                try
                {
                    var elevation = new TokenElevation();
                    if (GetTokenInformation(token, 20, ref elevation, Marshal.SizeOf<TokenElevation>(), out _))
                        elevated = elevation.TokenIsElevated != 0;
                    GetTokenInformation(token, 25, IntPtr.Zero, 0, out var required);
                    if (required > 0)
                    {
                        var buffer = Marshal.AllocHGlobal(required);
                        try
                        {
                            if (GetTokenInformation(token, 25, buffer, required, out _))
                            {
                                var label = Marshal.PtrToStructure<TokenMandatoryLabel>(buffer);
                                var countPointer = GetSidSubAuthorityCount(label.Label.Sid);
                                var count = Marshal.ReadByte(countPointer);
                                var rid = Marshal.ReadInt32(GetSidSubAuthority(label.Label.Sid, (uint)(count - 1)));
                                integrity = rid switch
                                {
                                    < 0x1000 => "untrusted",
                                    < 0x2000 => "low",
                                    < 0x3000 => "medium",
                                    < 0x4000 => "high",
                                    < 0x5000 => "system",
                                    _ => "protected"
                                };
                            }
                        }
                        finally { Marshal.FreeHGlobal(buffer); }
                    }
                }
                finally { CloseHandle(token); }
            }
            return new(architecture, elevated, integrity, null);
        }
        finally { CloseHandle(process); }
    }

    private static string MachineName(ushort machine) => machine switch
    {
        0x014c => "x86",
        0x8664 => "x64",
        0xaa64 => "arm64",
        0x01c4 => "arm",
        0 => "native",
        _ => "0x" + machine.ToString("x4", System.Globalization.CultureInfo.InvariantCulture)
    };

    private sealed record NativeProcessInfo(string? Architecture, bool? Elevated, string? IntegrityLevel, string? Error);
    [StructLayout(LayoutKind.Sequential)] private struct TokenElevation { public int TokenIsElevated; }
    [StructLayout(LayoutKind.Sequential)] private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenMandatoryLabel { public SidAndAttributes Label; }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int informationClass,
        ref TokenElevation information, int length, out int returnLength);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int informationClass,
        IntPtr information, int length, out int returnLength);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}
