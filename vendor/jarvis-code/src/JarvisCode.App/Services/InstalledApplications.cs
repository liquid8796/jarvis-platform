using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace JarvisCode.App.Services;

/// <summary>
/// The Start-menu applications <c>request_access</c> names in its own schema.
///
/// The reference builds its computer-use tool list from a live enumeration
/// (<c>executor.listInstalledApps()</c>) under a 1000ms budget — <c>Ne = 1e3</c>
/// in <c>index2.chunk-CEBgETf7.js</c> — and omits the list entirely when the
/// enumeration does not answer in time, which is why the tool doc has to work
/// with and without it. The result is cached and pre-warmed at start-up.
///
/// On Windows an "installed application" is a Start-menu entry: the reference
/// asks for "display names exactly as they appear in the Start menu", and a
/// capture of its own request lists entries like "Check for FxSound updates" and
/// "Console RAR manual" that exist nowhere but there. The Start menu is the
/// shell's <c>AppsFolder</c>, not the <c>.lnk</c> tree — a packaged (MSIX/Store)
/// application has no shortcut file at all, which is why a scan of the two
/// Programs folders alone misses Notepad, Calculator, Photos, Terminal and
/// Settings while <c>Get-StartApps</c> lists every one of them. Both are read and
/// unioned, since a shortcut the AppsFolder happens not to surface is still an
/// entry the user can see.
/// </summary>
internal static class InstalledApplications
{
    /// <summary>The reference's enumeration budget: over it, the list is omitted.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(1000);

    private static readonly object Gate = new();
    private static IReadOnlyList<string>? _cached;
    private static bool _scanning;

    /// <summary>Starts the one scan this process runs, under <see cref="Gate"/>.</summary>
    private static void StartScan()
    {
        if (!_scanning)
        {
            _scanning = true;
            Scan();
        }
    }

    /// <summary>
    /// What the last completed scan found, or an empty list while none has. A
    /// first read starts the scan and returns empty, which is the reference's
    /// own behaviour when its enumeration overruns the budget.
    /// </summary>
    public static IReadOnlyList<string> Names
    {
        get
        {
            lock (Gate)
            {
                if (_cached is { } ready)
                {
                    return ready;
                }

                StartScan();
                return [];
            }
        }
    }

    /// <summary>
    /// Starts the scan without waiting for it — the reference's <c>P()</c>
    /// pre-warm, so the first turn's tool doc already carries the list.
    /// </summary>
    public static void Prewarm()
    {
        lock (Gate)
        {
            if (_cached is null)
            {
                StartScan();
            }
        }
    }

    /// <summary>
    /// The application a request meant, or null when neither the index nor the
    /// running processes know one. The reference resolves what a request_access
    /// call asked for before showing the user anything, so a typo is answered
    /// rather than put in front of them as a grant they cannot judge.
    ///
    /// Its resolver (<c>Yt</c> in <c>index.chunk-b0FbQZTl.js</c>, desktop
    /// 1.44121.2.0) takes the installed list <em>and</em> the running one, and
    /// falls back to the running app keyed by its executable path and by that
    /// path's last segment — which is what makes "notepad.exe" resolve while the
    /// application is open, whether or not anything indexed it. Its own refusal
    /// says the name matched no "installed or running application"; this port
    /// carried that sentence while checking only the first half of it, so an app
    /// that was running and unindexed was refused by a message that had already
    /// told the model it would not be. Names arrive here through
    /// <see cref="ComputerUseGrants.Normalize"/>, which has already reduced a path
    /// and stripped ".exe", so the running half is one comparison against the
    /// process name rather than the reference's two maps.
    ///
    /// An empty index means the scan has not answered yet, and the reference's
    /// own list is then omitted too — so nothing is refused on the strength of
    /// an index that does not exist.
    /// </summary>
    internal static string? Resolve(
        string requested, IReadOnlyList<string> installed, IReadOnlyCollection<string>? running = null)
    {
        if (installed.Count == 0)
        {
            return requested;
        }

        if (installed.FirstOrDefault(name => name.Equals(requested, StringComparison.OrdinalIgnoreCase)) is { } indexed)
        {
            return indexed;
        }

        return running?.FirstOrDefault(name => name.Equals(requested, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The applications that are running with a window of their own, by process
    /// name — the reference's <c>listRunningApps()</c>, whose entries this
    /// platform identifies by executable rather than by bundle id. Read live: a
    /// cached answer would refuse an application the user opened a moment ago,
    /// which is the case the fallback exists for.
    /// </summary>
    internal static IReadOnlyCollection<string> Running()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (InvalidOperationException)
        {
            return names;
        }

        foreach (var process in processes)
        {
            try
            {
                // A window of its own is what makes an application something the
                // user could point at; a service has nothing to grant control of.
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    names.Add(process.ProcessName);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                or System.ComponentModel.Win32Exception)
            {
                // Exited between the listing and the read, or refuses to be asked.
            }
            finally
            {
                process.Dispose();
            }
        }

        return names;
    }

    /// <summary>
    /// The reference's <c>nn</c>, reduced to the two tiers this index can serve:
    /// an entry that contains the request, then one the request contains. Its
    /// third tier is an edit distance over bundle ids, which Windows has none of.
    /// </summary>
    internal static IReadOnlyList<string> DidYouMean(string requested, IReadOnlyList<string> installed)
    {
        var needle = requested.Trim().ToLowerInvariant();
        if (needle.Length < 3)
        {
            return [];
        }

        return
        [
            .. installed
                .Select(name => (Name: name, Lower: name.ToLowerInvariant()))
                .Select(candidate => (
                    candidate.Name,
                    Score: candidate.Lower.Contains(needle, StringComparison.Ordinal)
                        ? 1000 - Math.Min(50, Math.Abs(needle.Length - candidate.Lower.Length))
                        : candidate.Lower.Length >= 4 && needle.Contains(candidate.Lower, StringComparison.Ordinal)
                            ? 900 - Math.Min(50, Math.Abs(needle.Length - candidate.Lower.Length))
                            : 0))
                .Where(static candidate => candidate.Score > 0)
                .OrderByDescending(static candidate => candidate.Score)
                .ThenBy(static candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .Select(static candidate => candidate.Name)
                .Take(3),
        ];
    }

    /// <summary>Drops the cache, for tests and for a settings change that invalidates it.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            _cached = null;
            _scanning = false;
        }
    }

    private static void Scan()
    {
        // The AppsFolder is read through the shell's own COM object, which is
        // apartment-threaded: asked from the thread pool it would be marshalled
        // through a host apartment the runtime spins up for each call.
        var thread = new Thread(ScanCore) { IsBackground = true, Name = "installed-apps" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static void ScanCore()
    {
        IReadOnlyList<string> found;
        try
        {
            using var budget = new CancellationTokenSource(Budget);
            found = Enumerate(StartMenuRoots(), budget.Token);
        }
        catch (OperationCanceledException)
        {
            // Over budget. The reference omits the list rather than blocking, and
            // leaves the cache unset so a later turn can try again.
            return;
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        lock (Gate)
        {
            _cached = found;
        }
    }

    /// <summary>The per-user menu first, then the machine-wide one.</summary>
    internal static IEnumerable<string> StartMenuRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
    }

    /// <summary>
    /// Every shortcut under the given menus, by display name: deduplicated
    /// case-insensitively and ordered the same way, since one application is
    /// commonly pinned in both menus.
    ///
    /// The ordering is this port's own. A capture of the reference's list is
    /// alphabetical apart from a single leading entry, and one sample does not
    /// establish what hoisted it — so the set is reproduced and the order is
    /// declared rather than guessed at.
    /// </summary>
    internal static IReadOnlyList<string> Enumerate(
        IEnumerable<string> roots, CancellationToken cancellationToken)
    {
        SortedSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (var name in AppsFolderNames(cancellationToken))
        {
            Add(names, name);
        }

        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var extension = Path.GetExtension(file);
                if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Add(names, Path.GetFileNameWithoutExtension(file));
            }
        }

        return [.. names];
    }

    /// <summary>
    /// A name carrying a line break or an angle bracket would break out of the
    /// <c>&lt;installed-apps&gt;</c> block it is quoted inside.
    /// </summary>
    private static void Add(SortedSet<string> names, string name)
    {
        if (name.Length > 0 && name.AsSpan().IndexOfAny("\r\n<>") < 0)
        {
            names.Add(name);
        }
    }

    /// <summary>
    /// What the Start menu actually lists: the shell's <c>AppsFolder</c>, which
    /// carries packaged applications as well as the ones with a shortcut file.
    /// On this machine it answers 152 names where the two Programs folders hold
    /// 113 — "Notepad" among the difference, which is the entry a request_access
    /// call was refused for. Read late-bound rather than against a shell interop
    /// assembly, since this package takes no new dependency for one folder.
    /// </summary>
    internal static IEnumerable<string> AppsFolderNames(CancellationToken cancellationToken)
    {
        object? shell;
        object? items;
        int count;
        try
        {
            if (Type.GetTypeFromProgID("Shell.Application") is not { } type)
            {
                yield break;
            }

            shell = Activator.CreateInstance(type);
            var folder = shell is null ? null : Invoke(shell, "NameSpace", "shell:AppsFolder");
            items = folder is null ? null : Invoke(folder, "Items");
            count = items is null
                ? 0
                : Convert.ToInt32(Get(items, "Count"), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
            or MissingMethodException or TargetInvocationException or InvalidCastException
            or NotSupportedException or UnauthorizedAccessException)
        {
            // No shell to ask — the shortcut scan still answers.
            yield break;
        }

        for (var i = 0; i < count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            string? name;
            try
            {
                var item = Invoke(items!, "Item", i);
                name = item is null ? null : Get(item, "Name") as string;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                or MissingMethodException or TargetInvocationException or InvalidCastException)
            {
                continue;
            }

            if (name is { Length: > 0 })
            {
                yield return name;
            }
        }

        GC.KeepAlive(shell);
    }

    private static object? Invoke(object target, string member, params object[] args) =>
        target.GetType().InvokeMember(member, BindingFlags.InvokeMethod, null, target, args);

    private static object? Get(object target, string member) =>
        target.GetType().InvokeMember(member, BindingFlags.GetProperty, null, target, null);
}
