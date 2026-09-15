namespace JarvisCode.Core.Security;

/// <summary>Semantic bucket for a shell command, for labeling and prompts — never for gating.</summary>
public enum CommandIntent
{
    ReadOnly,
    Write,
    Destructive,
    Network,
    ProcessManagement,
    PackageManagement,
    SystemAdmin,
    Unknown,
}

/// <summary>
/// Classifies shell command lines (ported from claw-code's bash_validation/permission_enforcer,
/// extended for PowerShell). <see cref="IsReadOnly"/> is the hardened, fail-closed gate used to
/// auto-approve commands; <see cref="Classify"/> is a looser labeling heuristic that must never
/// authorize anything on its own.
/// </summary>
public static class ShellCommandInspector
{
    /// <summary>
    /// Any of these anywhere in the line defeats static analysis (chaining, substitution,
    /// redirection, subshells), so the gate fails closed. The set covers POSIX shells and
    /// PowerShell alike; the backtick doubles as PowerShell's escape character.
    /// </summary>
    private static readonly char[] ShellMetachars = [';', '|', '&', '$', '`', '>', '<', '(', ')', '{', '}', '\n'];

    /// <summary>Commands that only observe state. POSIX set from claw-code plus Windows/PowerShell equivalents.</summary>
    private static readonly HashSet<string> ReadOnlyCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        // POSIX (claw-code permission_enforcer allowlist)
        "cat", "head", "tail", "less", "more", "wc", "ls", "find", "grep", "rg",
        "awk", "sed", "echo", "printf", "which", "where", "whoami", "pwd",
        "env", "printenv", "date", "cal", "df", "du", "free", "uptime", "uname",
        "file", "stat", "diff", "sort", "uniq", "tr", "cut", "paste", "test",
        "true", "false", "type", "readlink", "realpath", "basename", "dirname",
        "sha256sum", "md5sum", "b3sum", "xxd", "hexdump", "od", "strings",
        "tree", "jq", "yq",
        // Windows / PowerShell read-only equivalents
        "dir", "findstr", "hostname", "ver", "vol", "tasklist", "systeminfo",
        "get-content", "get-childitem", "get-item", "get-itemproperty", "get-location",
        "get-date", "get-command", "get-process", "get-service", "get-host", "get-filehash",
        "select-string", "select-object", "measure-object", "compare-object",
        "test-path", "resolve-path", "split-path", "format-list", "format-table",
        "gc", "gci", "gi", "gl", "sls",
    };

    /// <summary>
    /// git subcommands that never mutate. This is claw-code's strict enforcer list —
    /// deliberately excluding stash/fetch/config/reflog, which mutate state.
    /// </summary>
    private static readonly HashSet<string> GitReadOnlySubcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "log", "diff", "show", "branch", "rev-parse",
        "ls-files", "blame", "describe", "tag", "remote",
    };

    private static readonly string[] FindActionFlags = ["-exec", "-execdir", "-delete", "-ok", "-fprintf"];

    /// <summary>
    /// True only when the command line is guaranteed non-mutating: no shell metacharacters,
    /// a leading token on the read-only allowlist (or a read-only git subcommand), no find
    /// actions, and no in-place editing flags. Safe to auto-approve and to run in plan mode.
    /// </summary>
    public static bool IsReadOnly(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;
        if (command.IndexOfAny(ShellMetachars) >= 0)
            return false;

        var tokens = Tokenize(command);
        if (tokens.Count == 0)
            return false;
        var first = NormalizeExecutable(tokens[0]);

        if (first.Equals("git", StringComparison.OrdinalIgnoreCase))
        {
            // The literal next token must be an allowed subcommand; "git -C x status"
            // fails closed on purpose — global flags defeat position-based analysis.
            return tokens.Count >= 2 && GitReadOnlySubcommands.Contains(tokens[1]);
        }

        if (first.Equals("find", StringComparison.OrdinalIgnoreCase) &&
            FindActionFlags.Any(flag => command.Contains(flag, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (!ReadOnlyCommands.Contains(first))
            return false;

        // In-place editors mutate through an allowlisted name.
        if ((first.Equals("sed", StringComparison.OrdinalIgnoreCase) ||
             first.Equals("perl", StringComparison.OrdinalIgnoreCase)) &&
            tokens.Skip(1).Any(t => t.StartsWith("-i", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        if (command.Contains("--in-place", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    // ---------------- Destructive detection (warn, never block) ----------------

    /// <summary>Substring patterns checked against the whitespace-collapsed, lowercased command.</summary>
    private static readonly (string Pattern, string Warning)[] DestructivePatterns =
    [
        ("rm -rf /", "Recursive forced deletion at root — this can destroy the system"),
        ("rm -rf ~", "Recursive forced deletion of the home directory"),
        ("rm -rf *", "Recursive forced deletion of everything in the current directory"),
        ("rm -rf .", "Recursive forced deletion of the current directory"),
        ("mkfs", "Filesystem creation destroys existing data on the device"),
        ("dd if=", "Direct disk write — can overwrite partitions or devices"),
        ("> /dev/sd", "Writing to a raw disk device"),
        ("chmod -r 777", "Recursively setting world-writable permissions"),
        (":(){ :|:& };:", "Fork bomb — will crash the system"),
        // Windows
        ("rd /s", "Recursive directory deletion"),
        ("rmdir /s", "Recursive directory deletion"),
        ("del /f /s", "Forced recursive file deletion"),
        ("del /s /q", "Silent recursive file deletion"),
        ("format ", "Formatting a volume destroys its data"),
        ("diskpart", "Disk partitioning can destroy volumes"),
        ("vssadmin delete shadows", "Deletes volume shadow copies (restore points)"),
        ("cipher /w", "Wipes free space irreversibly"),
        ("reg delete hklm", "Deletes machine-wide registry keys"),
        ("bcdedit", "Modifies boot configuration"),
        // High-consequence git operations
        ("push --force", "Force push rewrites remote history"),
        ("push -f", "Force push rewrites remote history"),
        ("reset --hard", "Hard reset discards uncommitted work"),
        ("clean -fd", "git clean deletes untracked files permanently"),
        ("checkout -- .", "Discards all uncommitted changes"),
        ("restore .", "Discards uncommitted changes"),
    ];

    private static readonly HashSet<string> AlwaysDestructiveCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "shred", "wipefs", "mkfs", "format", "diskpart",
    };

    /// <summary>
    /// Human-readable warning when the command matches a destructive pattern; null when clean.
    /// Advisory only — shown on the confirmation prompt, it never blocks by itself.
    /// </summary>
    public static string? DescribeDestructive(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;
        var normalized = string.Join(' ', Tokenize(command)).ToLowerInvariant();

        foreach (var (pattern, warning) in DestructivePatterns)
        {
            if (normalized.Contains(pattern, StringComparison.Ordinal))
                return warning;
        }

        var tokens = Tokenize(command);
        var first = NormalizeExecutable(tokens[0]);
        if (AlwaysDestructiveCommands.Contains(first))
            return $"'{first}' is inherently destructive and can cause data loss";

        // Flag-order-independent recursive+forced deletion, token-level to avoid
        // matching words like "transform" or flags like "--dry-run".
        if (IsDeletionCommand(first))
        {
            var flags = tokens.Skip(1).ToList();
            bool recursive = flags.Any(IsRecursiveFlag);
            bool forced = flags.Any(IsForceFlag);
            if (recursive && forced)
                return "Recursive forced deletion — verify the target path is correct";
            if (first is "rd" or "rmdir" && flags.Any(f => f.Equals("/s", StringComparison.OrdinalIgnoreCase)))
                return "Recursive directory deletion — verify the target path is correct";
        }
        return null;
    }

    private static bool IsDeletionCommand(string first) =>
        first.ToLowerInvariant() is "rm" or "del" or "erase" or "rd" or "rmdir" or "remove-item" or "ri";

    private static bool IsRecursiveFlag(string token)
    {
        if (token.Equals("--recursive", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("-recurse", StringComparison.OrdinalIgnoreCase) ||
            token.StartsWith("-rec", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("/s", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        // Combined single-dash POSIX flags: -r, -rf, -fR, …
        return token.Length >= 2 && token[0] == '-' && token[1] != '-' &&
               token[1..].Contains('r', StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsForceFlag(string token)
    {
        if (token.Equals("--force", StringComparison.OrdinalIgnoreCase) ||
            token.StartsWith("-fo", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("/q", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("/f", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return token.Length >= 2 && token[0] == '-' && token[1] != '-' &&
               token[1..].Contains('f', StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- Intent classification (labeling only) ----------------

    private static readonly HashSet<string> SemanticReadOnly = new(ReadOnlyCommands, StringComparer.OrdinalIgnoreCase)
    {
        "egrep", "fgrep", "whereis", "whatis", "man", "info", "id", "groups",
        "cmp", "bc", "expr", "seq", "yes", "tput", "column", "xargs",
    };

    private static readonly HashSet<string> WriteCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "cp", "mv", "rm", "mkdir", "rmdir", "touch", "chmod", "chown", "chgrp", "ln", "install",
        "tee", "truncate", "shred", "mkfifo", "mknod", "dd",
        "copy", "move", "del", "erase", "rd", "md", "ren", "rename", "attrib", "icacls", "robocopy", "xcopy",
        "new-item", "copy-item", "move-item", "remove-item", "rename-item", "set-content", "add-content",
        "out-file", "set-itemproperty", "ri", "ni",
    };

    private static readonly HashSet<string> NetworkCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "curl", "wget", "ssh", "scp", "rsync", "ftp", "sftp", "nc", "ncat", "telnet", "ping",
        "traceroute", "dig", "nslookup", "host", "whois", "ifconfig", "ip", "netstat", "ss", "nmap",
        "invoke-webrequest", "invoke-restmethod", "iwr", "irm", "tracert", "ipconfig",
    };

    private static readonly HashSet<string> ProcessCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "kill", "pkill", "killall", "ps", "top", "htop", "bg", "fg", "jobs", "nohup", "disown",
        "wait", "nice", "renice",
        "taskkill", "stop-process", "start-process", "wait-process",
    };

    private static readonly HashSet<string> PackageCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "apt", "apt-get", "yum", "dnf", "pacman", "brew", "pip", "pip3", "npm", "yarn", "pnpm",
        "bun", "cargo", "gem", "go", "rustup", "snap", "flatpak",
        "winget", "choco", "scoop", "nuget", "dotnet",
    };

    private static readonly HashSet<string> SystemAdminCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "sudo", "su", "chroot", "mount", "umount", "fdisk", "parted", "lsblk", "blkid", "systemctl",
        "service", "journalctl", "dmesg", "modprobe", "insmod", "rmmod", "iptables", "ufw",
        "firewall-cmd", "sysctl", "crontab", "at", "useradd", "userdel", "usermod", "groupadd",
        "groupdel", "passwd", "visudo",
        "reg", "sc", "netsh", "schtasks", "runas", "net", "set-executionpolicy", "gpupdate",
    };

    /// <summary>
    /// Best-effort semantic bucket for display labels. Unlike <see cref="IsReadOnly"/> this ignores
    /// metacharacters, so it MUST NOT be used to authorize anything.
    /// </summary>
    public static CommandIntent Classify(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return CommandIntent.Unknown;
        var tokens = Tokenize(command);
        var first = NormalizeExecutable(tokens[0]);

        if (SemanticReadOnly.Contains(first))
        {
            bool inPlace = tokens.Skip(1).Any(t => t.StartsWith("-i", StringComparison.OrdinalIgnoreCase));
            return first.Equals("sed", StringComparison.OrdinalIgnoreCase) && inPlace
                ? CommandIntent.Write
                : CommandIntent.ReadOnly;
        }
        if (AlwaysDestructiveCommands.Contains(first) || IsDeletionCommand(first))
            return CommandIntent.Destructive;
        if (WriteCommands.Contains(first))
            return CommandIntent.Write;
        if (NetworkCommands.Contains(first))
            return CommandIntent.Network;
        if (ProcessCommands.Contains(first))
            return CommandIntent.ProcessManagement;
        if (PackageCommands.Contains(first))
            return CommandIntent.PackageManagement;
        if (SystemAdminCommands.Contains(first))
            return CommandIntent.SystemAdmin;
        if (first.Equals("git", StringComparison.OrdinalIgnoreCase))
        {
            var sub = tokens.Skip(1).FirstOrDefault(t => !t.StartsWith('-'));
            return sub is not null && GitReadOnlySubcommands.Contains(sub)
                ? CommandIntent.ReadOnly
                : CommandIntent.Write;
        }
        return CommandIntent.Unknown;
    }

    // ---------------- Token helpers ----------------

    /// <summary>Whitespace tokens with leading KEY=value environment assignments stripped.</summary>
    private static List<string> Tokenize(string command)
    {
        var tokens = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        while (tokens.Count > 0 && IsEnvAssignment(tokens[0]))
            tokens.RemoveAt(0);
        return tokens;
    }

    private static bool IsEnvAssignment(string token)
    {
        int eq = token.IndexOf('=');
        if (eq <= 0)
            return false;
        return token[..eq].All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    }

    /// <summary>Bare executable name: directory prefix stripped (both separators) and .exe/.cmd/.bat removed.</summary>
    private static string NormalizeExecutable(string token)
    {
        var name = token;
        int cut = name.LastIndexOfAny(['/', '\\']);
        if (cut >= 0)
            name = name[(cut + 1)..];
        foreach (var extension in (string[])[".exe", ".cmd", ".bat", ".com"])
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^extension.Length];
                break;
            }
        }
        return name;
    }
}
