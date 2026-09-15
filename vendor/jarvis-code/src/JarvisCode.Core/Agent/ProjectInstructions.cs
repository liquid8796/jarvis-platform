using System.Text.RegularExpressions;
using JarvisCode.Core.Customization;

namespace JarvisCode.Core.Agent;

/// <summary>
/// Which tier an instruction file was discovered in. The reference keeps the
/// same four (its Managed/User/Project/Local); the label a file is rendered
/// under, whether an exclude pattern may hide it, and what a hook is told about
/// it all follow from this and nothing else.
/// </summary>
public enum InstructionMemoryType
{
    /// <summary>Organization policy: the managed directory, or settings-supplied text.</summary>
    Managed,

    /// <summary>The user's own instructions, in force in every project.</summary>
    User,

    /// <summary>Checked into the repository.</summary>
    Project,

    /// <summary>The user's private per-project overlay, not checked in.</summary>
    Local,
}

/// <summary>
/// Loads the instruction files a turn runs under, ported from the reference
/// CLI's memory loader (2.1.251: <c>BXn</c> eager, <c>Bg</c> one file plus its
/// <c>@</c>-imports, <c>LM</c> a rules directory, <c>Sxt</c>/<c>$1t</c> the
/// nested load a touched file triggers).
///
/// Four tiers in the reference's order — Managed, User, then the ancestor chain
/// of the working directory root-first, then the directories added to the
/// session — with the reference's own file names swapped for this app's:
/// <c>JARVIS.md</c> where the reference reads <c>CLAUDE.md</c>, <c>.jarvis/</c>
/// where it reads <c>.claude/</c>. The reference spellings stay readable in the
/// same slot so a repository written for Claude Code keeps working; the Jarvis
/// name shadows the Claude one when both exist.
/// </summary>
public static class ProjectInstructions
{
    /// <summary>
    /// A directory's own instructions, in resolution order. Only the first that
    /// exists is read: the two names are one slot, not two files.
    /// </summary>
    public static readonly IReadOnlyList<string> FileNames = ["JARVIS.md", "CLAUDE.md"];

    /// <summary>The private overlay beside them, same one-slot rule.</summary>
    public static readonly IReadOnlyList<string> LocalFileNames = ["JARVIS.local.md", "CLAUDE.local.md"];

    /// <summary>Per-project configuration directories, same one-slot rule.</summary>
    public static readonly IReadOnlyList<string> ConfigDirectoryNames = [".jarvis", ".claude"];

    /// <summary>The rules folder inside a configuration directory (and inside the user's).</summary>
    public const string RulesDirectoryName = "rules";

    /// <summary>
    /// Largest instruction file that is read at all (the reference's
    /// <c>Yie</c>). A bigger one is skipped whole rather than cut — half a rules
    /// file can contradict the half that was dropped.
    /// </summary>
    public const long MaxFileBytes = 4L * 1024 * 1024;

    /// <summary>
    /// How deep <c>@</c>-imports are followed before the chain stops, counting
    /// the importing file as depth 0 (the reference's <c>DXn</c>).
    /// </summary>
    public const int MaxImportDepth = 5;

    /// <summary>
    /// The pseudo-path the reference files settings-supplied policy text under,
    /// so a rendered block names something rather than an empty string.
    /// </summary>
    public const string ManagedSettingsPath = "<managed-settings>";

    /// <summary>
    /// Kill switch, read for raw presence as the reference reads it. The name is
    /// the reference's, like every other <c>CLAUDE_CODE_*</c> variable this port
    /// honours, because a script written against the reference sets that one.
    /// </summary>
    public const string DisableEnvironmentVariable = "CLAUDE_CODE_DISABLE_CLAUDE_MDS";

    /// <summary>
    /// Gate on reading instruction files out of the session's added directories,
    /// again under the reference's own name.
    /// </summary>
    public const string AdditionalDirectoriesEnvironmentVariable =
        "CLAUDE_CODE_ADDITIONAL_DIRECTORIES_CLAUDE_MD";

    /// <summary>
    /// Size past which an instruction file is worth reporting as large. The
    /// reference's rule is max(40_000, window x 5% x chars-per-token); this is
    /// its floor, and <see cref="LargeFileThreshold"/> applies the window.
    /// Nothing is truncated at it - it only decides what /doctor mentions.
    /// </summary>
    public const int LargeFileFloorChars = 40_000;

    private const double LargeFileWindowFraction = 0.05;

    /// <summary>The reference's own estimate; it uses 3 for a few older families.</summary>
    private const int CharsPerToken = 4;

    /// <summary>
    /// Extensions an <c>@</c>-import may name (the reference's <c>nXn</c>).
    /// Anything else is skipped rather than pulled into the prompt as text.
    /// </summary>
    private static readonly HashSet<string> ImportableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".text", ".json", ".yaml", ".yml", ".toml", ".xml", ".csv", ".html", ".htm",
        ".css", ".scss", ".sass", ".less", ".js", ".ts", ".tsx", ".jsx", ".mjs", ".cjs", ".mts",
        ".cts", ".py", ".pyi", ".pyw", ".rb", ".erb", ".rake", ".go", ".rs", ".java", ".kt", ".kts",
        ".scala", ".c", ".cpp", ".cc", ".cxx", ".h", ".hpp", ".hxx", ".cs", ".swift", ".sh", ".bash",
        ".zsh", ".fish", ".ps1", ".bat", ".cmd", ".env", ".ini", ".cfg", ".conf", ".config",
        ".properties", ".sql", ".graphql", ".gql", ".proto", ".vue", ".svelte", ".astro", ".ejs",
        ".hbs", ".pug", ".jade", ".php", ".pl", ".pm", ".lua", ".r", ".R", ".dart", ".ex", ".exs",
        ".erl", ".hrl", ".clj", ".cljs", ".cljc", ".edn", ".hs", ".lhs", ".elm", ".ml", ".mli",
        ".f", ".f90", ".f95", ".for", ".cmake", ".make", ".makefile", ".gradle", ".sbt", ".rst",
        ".adoc", ".asciidoc", ".org", ".tex", ".latex", ".lock", ".log", ".diff", ".patch",
    };

    /// <summary>
    /// The reference's import scanner: an <c>@</c> at the start of a line or
    /// after whitespace, taking everything up to the next unescaped space.
    /// </summary>
    private static readonly Regex ImportPattern = new(
        @"(?:^|\s)@((?:[^\s\\]|\\ )+)", RegexOptions.Compiled);

    private static readonly Regex HtmlCommentPattern = new(
        @"<!--[\s\S]*?-->", RegexOptions.Compiled);

    /// <summary>
    /// The size at which <paramref name="maxContextTokens"/> makes a file worth
    /// warning about, as the reference computes it.
    /// </summary>
    public static int LargeFileThreshold(int maxContextTokens) =>
        maxContextTokens <= 0
            ? LargeFileFloorChars
            : Math.Max(
                LargeFileFloorChars,
                (int)Math.Round(maxContextTokens * LargeFileWindowFraction * CharsPerToken));

    /// <summary>
    /// One loaded file. <paramref name="Globs"/> is the <c>paths:</c> frontmatter
    /// that made a rule conditional (null when it is unconditional), and
    /// <paramref name="ParentFilePath"/> the file that <c>@</c>-imported this one.
    /// </summary>
    public sealed record InstructionFile(
        string FilePath,
        string Content,
        InstructionMemoryType Type = InstructionMemoryType.Project,
        IReadOnlyList<string>? Globs = null,
        string? ParentFilePath = null);

    public sealed record InstructionSet(IReadOnlyList<InstructionFile> Files)
    {
        public static readonly InstructionSet Empty = new([]);
        public bool IsEmpty => Files.Count == 0;
    }

    /// <summary>
    /// Everything the loader needs that is not the working directory: where this
    /// installation keeps the user's and the organization's instructions, which
    /// directories the session added, and the two switches the user controls.
    /// Core stays path-parameterized, so the host supplies the first two.
    /// </summary>
    public sealed record InstructionScope
    {
        public required string WorkingDirectory { get; init; }

        /// <summary>This installation's per-user configuration directory, or null.</summary>
        public string? UserDirectory { get; init; }

        /// <summary>The machine-wide policy directory, or null.</summary>
        public string? ManagedDirectory { get; init; }

        /// <summary>Policy text supplied by settings rather than by a file.</summary>
        public string? ManagedInstructions { get; init; }

        /// <summary>Directories added to the session with /add-dir.</summary>
        public IReadOnlyList<string> AdditionalDirectories { get; init; } = [];

        /// <summary>
        /// Glob patterns (or absolute paths) whose files are not loaded. Applies
        /// to the User, Project and Local tiers only: a policy file cannot be
        /// excluded by the machine it governs.
        /// </summary>
        public IReadOnlyList<string> Excludes { get; init; } = [];

        /// <summary>
        /// Whether an <c>@</c>-import may reach outside the working directory.
        /// Off by default: a third-party repository's instructions would
        /// otherwise be able to name any file on the machine.
        /// </summary>
        public bool AllowExternalImports { get; init; }

        public static InstructionScope ForWorkingDirectory(string workingDirectory) =>
            new() { WorkingDirectory = workingDirectory };
    }

    /// <summary>
    /// The half of the scope that does not change with the working directory:
    /// where this installation keeps the user's and the organization's
    /// instructions, and the two switches. The host installs it once at startup
    /// so a subagent, a skill or /doctor loading by path alone still sees every
    /// tier — Core keeps taking the paths from outside rather than knowing them.
    /// </summary>
    public static InstructionScope HostScope { get; set; } =
        new() { WorkingDirectory = string.Empty };

    /// <summary>
    /// Whether this project has said yes to imports that reach outside it. The
    /// reference keeps that answer per project and asks for it once; the host
    /// owns where it is stored, so it answers here.
    /// </summary>
    public static Func<string, bool> ExternalImportsApproved { get; set; } = static _ => false;

    /// <summary>The installed host scope, pointed at one working directory.</summary>
    public static InstructionScope ScopeFor(
        string workingDirectory, IReadOnlyList<string>? additionalDirectories = null) =>
        HostScope with
        {
            WorkingDirectory = workingDirectory,
            AdditionalDirectories = additionalDirectories ?? HostScope.AdditionalDirectories,
            AllowExternalImports = ExternalImportsApproved(workingDirectory),
        };

    /// <summary>One import that reaches outside the working directory.</summary>
    public sealed record ExternalImport(string FilePath, string ParentFilePath);

    /// <summary>
    /// The imports a load would have to reach outside the working directory for,
    /// which is what the reference shows when it asks whether to allow them (its
    /// a4e): a file some other file imported, outside the project, in a tier
    /// other than the user's own — the user's may always import from anywhere.
    /// </summary>
    public static IReadOnlyList<ExternalImport> ExternalImports(InstructionScope scope)
    {
        if (Disabled)
            return [];
        var probed = LoadAll(scope with { AllowExternalImports = true });
        return
        [
            .. probed.Files
                .Where(file => file.ParentFilePath is not null &&
                               file.Type != InstructionMemoryType.User &&
                               !IsUnder(file.FilePath, scope.WorkingDirectory))
                .Select(file => new ExternalImport(file.FilePath, file.ParentFilePath!)),
        ];
    }

    /// <summary>True while the reference's kill switch is set in the environment.</summary>
    public static bool Disabled =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(DisableEnvironmentVariable));

    private static bool AdditionalDirectoriesEnabled =>
        IsTruthy(Environment.GetEnvironmentVariable(AdditionalDirectoriesEnvironmentVariable));

    /// <summary>The reference reads this family of variables for raw truthiness.</summary>
    private static bool IsTruthy(string? value) =>
        value is { Length: > 0 } && value != "0" && !value.Equals("false", StringComparison.OrdinalIgnoreCase);

    /// <summary>The full eager load for one working directory, under the host scope.</summary>
    public static InstructionSet LoadAll(string workingDirectory) =>
        LoadAll(ScopeFor(workingDirectory));

    /// <summary>The full eager load, in the reference's own order.</summary>
    public static InstructionSet LoadAll(InstructionScope scope)
    {
        if (Disabled)
            return InstructionSet.Empty;

        var state = new LoadState(scope);
        try
        {
            var files = new List<InstructionFile>();

            // Managed: the policy file, the policy text settings carry, then the
            // organization's rules directory.
            if (scope.ManagedDirectory is { Length: > 0 } managed)
            {
                AddSlot(files, state, managed, FileNames, InstructionMemoryType.Managed);
                AddRulesDirectory(
                    files, state, RulesDirectoryIn(managed, forUser: false), InstructionMemoryType.Managed,
                    conditional: false);
            }

            if (scope.ManagedInstructions is { Length: > 0 } policyText &&
                policyText.Trim() is { Length: > 0 } trimmedPolicy)
            {
                files.Add(new InstructionFile(
                    ManagedSettingsPath, trimmedPolicy, InstructionMemoryType.Managed));
            }

            // User: in force in every project, and allowed to import from
            // anywhere — the files it reaches are the user's own.
            if (scope.UserDirectory is { Length: > 0 } user)
            {
                AddSlot(files, state, user, FileNames, InstructionMemoryType.User, externalImports: true);
                AddRulesDirectory(
                    files, state, RulesDirectoryIn(user, forUser: true), InstructionMemoryType.User,
                    conditional: false, externalImports: true);
            }

            // The working directory's ancestor chain, outermost first.
            var worktree = WorktreeLayout.For(scope.WorkingDirectory);
            foreach (var directory in AncestorChain(scope.WorkingDirectory))
                AddProjectDirectory(files, state, directory, skipProject: worktree.IsMainRepoOnly(directory));

            // Directories the session added, behind the reference's own gate.
            if (AdditionalDirectoriesEnabled)
            {
                foreach (var directory in scope.AdditionalDirectories)
                    AddProjectDirectory(files, state, directory, skipProject: false);
            }

            return files.Count == 0 ? InstructionSet.Empty : new InstructionSet(files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return InstructionSet.Empty;
        }
    }

    /// <summary>
    /// The instructions a touched file brings in that the eager load did not
    /// (the reference's <c>Sxt</c>): every directory between the working
    /// directory and the file contributes its own instructions, and a rule whose
    /// <c>paths:</c> frontmatter matches the file wakes up wherever it lives.
    /// </summary>
    public static IReadOnlyList<InstructionFile> LoadForTouchedFile(
        InstructionScope scope, string filePath, ISet<string> alreadyLoaded)
    {
        if (Disabled)
            return [];

        try
        {
            var full = Path.GetFullPath(filePath, scope.WorkingDirectory);
            var state = new LoadState(scope);
            var files = new List<InstructionFile>();

            // Conditional rules from the tiers that live outside the project.
            if (scope.ManagedDirectory is { Length: > 0 } managed)
            {
                AddConditionalRules(
                    files, state, RulesDirectoryIn(managed, forUser: false), InstructionMemoryType.Managed, full);
            }

            if (scope.UserDirectory is { Length: > 0 } user)
            {
                AddConditionalRules(
                    files, state, RulesDirectoryIn(user, forUser: true), InstructionMemoryType.User, full,
                    externalImports: true);
            }

            var worktree = WorktreeLayout.For(scope.WorkingDirectory);
            var (nested, cwdLevel) = TouchedDirectories(scope.WorkingDirectory, full);

            // Every directory under the working directory on the way to the file
            // contributes the whole project/local slot, not just its rules.
            foreach (var directory in nested)
                AddTouchedDirectory(files, state, directory, full, skipProject: worktree.IsMainRepoOnly(directory));

            // The working directory and its ancestors already contributed their
            // unconditional instructions eagerly; only their conditional rules
            // can be new.
            foreach (var directory in cwdLevel)
            {
                if (worktree.IsMainRepoOnly(directory))
                    continue;
                AddConditionalRules(
                    files, state, RulesDirectoryIn(directory, forUser: false), InstructionMemoryType.Project, full);
            }

            // Compared canonically: the caller's set and this walk can spell the
            // same file differently, and a file counted twice is a file the
            // model reads twice.
            var seen = new HashSet<string>(alreadyLoaded.Select(Canonical), StringComparer.OrdinalIgnoreCase);
            return [.. files.Where(file => !seen.Contains(Canonical(file.FilePath)))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>
    /// The instructions a directory brings with it, used when the session moves
    /// (the reference's own answer to "its CLAUDE.md, if any, follows below").
    /// Every directory from the filesystem root down to it is asked, as the
    /// reference asks, so a nested project inherits its parents.
    /// </summary>
    public static InstructionSet LoadForDirectory(InstructionScope scope, string directory)
    {
        if (Disabled)
            return InstructionSet.Empty;

        try
        {
            var state = new LoadState(scope);
            var files = new List<InstructionFile>();
            var worktree = WorktreeLayout.For(directory);
            foreach (var ancestor in AncestorChain(directory))
                AddTouchedDirectory(files, state, ancestor, null, skipProject: worktree.IsMainRepoOnly(ancestor));
            return files.Count == 0 ? InstructionSet.Empty : new InstructionSet(files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException or ArgumentException)
        {
            return InstructionSet.Empty;
        }
    }

    // ---------------------------------------------------------------- tiers

    /// <summary>
    /// One project directory's whole contribution: its own instructions, the
    /// copy inside its configuration directory, its rules folder, and the
    /// private overlay beside them.
    /// </summary>
    private static void AddProjectDirectory(
        List<InstructionFile> files, LoadState state, string directory, bool skipProject)
    {
        if (!skipProject)
        {
            AddSlot(files, state, directory, FileNames, InstructionMemoryType.Project);
            AddConfigDirectorySlot(files, state, directory, InstructionMemoryType.Project);
            AddRulesDirectory(
                files, state, RulesDirectoryIn(directory, forUser: false), InstructionMemoryType.Project,
                conditional: false);
        }

        AddSlot(files, state, directory, LocalFileNames, InstructionMemoryType.Local);
    }

    /// <summary>
    /// The same contribution as <see cref="AddProjectDirectory"/> plus the rules
    /// a touched file activates — the reference's <c>$1t</c>, which walks the
    /// rules folder twice from the same starting set so the conditional pass
    /// still sees files the unconditional pass consumed.
    /// </summary>
    private static void AddTouchedDirectory(
        List<InstructionFile> files, LoadState state, string directory, string? triggerFilePath, bool skipProject)
    {
        if (!skipProject)
        {
            AddSlot(files, state, directory, FileNames, InstructionMemoryType.Project);
            AddConfigDirectorySlot(files, state, directory, InstructionMemoryType.Project);
        }

        AddSlot(files, state, directory, LocalFileNames, InstructionMemoryType.Local);

        if (skipProject)
            return;

        var rules = RulesDirectoryIn(directory, forUser: false);
        var forked = state.ForkProcessedPaths();
        AddRulesDirectory(files, forked, rules, InstructionMemoryType.Project, conditional: false);
        if (triggerFilePath is not null)
            AddConditionalRules(files, state, rules, InstructionMemoryType.Project, triggerFilePath);
        state.MergeProcessedPaths(forked);
    }

    /// <summary>Reads the first of <paramref name="names"/> that exists in the directory.</summary>
    private static void AddSlot(
        List<InstructionFile> files, LoadState state, string directory, IReadOnlyList<string> names,
        InstructionMemoryType type, bool externalImports = false)
    {
        foreach (var name in names)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path))
                continue;
            AddFile(files, state, path, type, externalImports);
            return;
        }
    }

    /// <summary>
    /// The copy of a directory's instructions that lives inside its
    /// configuration directory (<c>.jarvis/JARVIS.md</c>, or the reference's
    /// <c>.claude/CLAUDE.md</c>). The reference loads this beside the one at the
    /// directory root, not instead of it.
    /// </summary>
    private static void AddConfigDirectorySlot(
        List<InstructionFile> files, LoadState state, string directory, InstructionMemoryType type)
    {
        for (int i = 0; i < ConfigDirectoryNames.Count; i++)
        {
            var path = Path.Combine(directory, ConfigDirectoryNames[i], FileNames[i]);
            if (!File.Exists(path))
                continue;
            AddFile(files, state, path, type, externalImports: false);
            return;
        }
    }

    /// <summary>
    /// The rules directory for a project or configuration directory: the Jarvis
    /// one when it exists, else the reference's.
    /// </summary>
    private static string RulesDirectoryIn(string directory, bool forUser)
    {
        if (forUser)
            return Path.Combine(directory, RulesDirectoryName);
        foreach (var config in ConfigDirectoryNames)
        {
            var candidate = Path.Combine(directory, config, RulesDirectoryName);
            if (Directory.Exists(candidate))
                return candidate;
        }
        return Path.Combine(directory, ConfigDirectoryNames[0], RulesDirectoryName);
    }

    /// <summary>
    /// Walks a rules directory, keeping the unconditional files or exactly the
    /// conditional ones (the reference's <c>LM</c>, whose <c>conditionalRule</c>
    /// flag inverts the same filter).
    /// </summary>
    private static void AddRulesDirectory(
        List<InstructionFile> files, LoadState state, string directory, InstructionMemoryType type,
        bool conditional, bool externalImports = false)
    {
        if (!Directory.Exists(directory))
            return;

        var walked = new List<InstructionFile>();
        WalkRules(walked, state, directory, type, externalImports, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        foreach (var file in walked)
        {
            if (conditional == (file.Globs is { Count: > 0 }))
                files.Add(file);
        }
    }

    private static void WalkRules(
        List<InstructionFile> files, LoadState state, string directory, InstructionMemoryType type,
        bool externalImports, HashSet<string> visitedDirectories)
    {
        if (!visitedDirectories.Add(Canonical(directory)))
            return;

        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Array.Sort(entries, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (Directory.Exists(entry))
                WalkRules(files, state, entry, type, externalImports, visitedDirectories);
            else if (entry.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                AddFile(files, state, entry, type, externalImports);
        }
    }

    /// <summary>
    /// The conditional half of a rules directory, filtered to the rules whose
    /// <c>paths:</c> patterns match the touched file. The reference measures that
    /// path from the directory holding the configuration folder for a project
    /// rule, and from the working directory for the user's and the
    /// organization's.
    /// </summary>
    private static void AddConditionalRules(
        List<InstructionFile> files, LoadState state, string rulesDirectory, InstructionMemoryType type,
        string triggerFilePath, bool externalImports = false)
    {
        if (!Directory.Exists(rulesDirectory))
            return;

        var basis = type == InstructionMemoryType.Project
            ? Path.GetDirectoryName(Path.GetDirectoryName(rulesDirectory.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            : state.Scope.WorkingDirectory;
        if (basis is not { Length: > 0 })
            return;

        string relative;
        try
        {
            relative = Path.GetRelativePath(basis, triggerFilePath);
        }
        catch (ArgumentException)
        {
            return;
        }

        if (relative.Length == 0 || relative.StartsWith("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            return;
        }

        var candidates = new List<InstructionFile>();
        AddRulesDirectory(candidates, state, rulesDirectory, type, conditional: true, externalImports);
        foreach (var rule in candidates)
        {
            if (rule.Globs is { Count: > 0 } globs && SkillInvocation.MatchesPaths(globs, relative))
                files.Add(rule);
        }
    }

    // ------------------------------------------------------------ one file

    /// <summary>
    /// Reads one instruction file and everything it <c>@</c>-imports, depth-first
    /// and depth-capped, refusing a second visit to a path already loaded (the
    /// reference's <c>Bg</c>).
    /// </summary>
    private static void AddFile(
        List<InstructionFile> files, LoadState state, string path, InstructionMemoryType type,
        bool externalImports, int depth = 0, string? parent = null)
    {
        if (depth >= MaxImportDepth)
            return;

        if (IsExcluded(state.Scope, path, type))
            return;
        if (!state.ProcessedPaths.Add(Canonical(path)))
            return;

        bool allowExternal = externalImports || state.Scope.AllowExternalImports;
        if (depth > 0 && !allowExternal && !IsUnder(path, state.Scope.WorkingDirectory))
            return;

        if (ReadInstructionFile(path) is not { Length: > 0 } raw)
            return;

        var (content, globs) = SplitConditionalFrontmatter(raw);
        var body = content.Contains("<!--", StringComparison.Ordinal)
            ? HtmlCommentPattern.Replace(content, string.Empty).Trim()
            : content.Trim();
        if (body.Length == 0)
            return;

        files.Add(new InstructionFile(path, body, type, globs, parent));

        foreach (var import in ExtractImports(body, path))
        {
            if (!allowExternal && !IsUnder(import, state.Scope.WorkingDirectory))
                continue;

            AddFile(files, state, import, type, externalImports, depth + 1, path);
        }
    }

    /// <summary>
    /// Reads one instruction file, or null when it is absent, unreadable, or over
    /// <see cref="MaxFileBytes"/> - an oversized file is skipped whole, never cut.
    /// A file whose extension names something that is not text is skipped too,
    /// which is what keeps an <c>@</c>-import from pulling in a binary.
    /// </summary>
    private static string? ReadInstructionFile(string path)
    {
        try
        {
            var extension = Path.GetExtension(path);
            if (extension.Length > 0 && !ImportableExtensions.Contains(extension))
                return null;
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes)
                return null;
            return File.ReadAllText(path).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Splits a rule's <c>paths:</c> frontmatter off its body (the reference's
    /// <c>rXn</c>): a trailing <c>/**</c> is dropped from each pattern, and a
    /// list that is empty or says nothing but <c>**</c> leaves the file
    /// unconditional rather than making it match everything.
    /// </summary>
    private static (string Content, IReadOnlyList<string>? Globs) SplitConditionalFrontmatter(string raw)
    {
        if (!raw.StartsWith("---", StringComparison.Ordinal))
            return (raw, null);

        var parsed = Frontmatter.ParseRich(raw);
        var declared = Frontmatter.GetList(parsed, "paths") ?? [];
        if (declared.Count == 0 && Frontmatter.Get(parsed.Fields, "paths") is { Length: > 0 } inline)
            declared = [.. inline.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        if (declared.Count == 0)
            return (parsed.Body.Trim(), null);

        var globs = declared
            .Select(pattern => pattern.EndsWith("/**", StringComparison.Ordinal) ? pattern[..^3] : pattern)
            .Where(pattern => pattern.Length > 0)
            .ToList();
        if (globs.Count == 0 || globs.All(pattern => pattern == "**"))
            return (parsed.Body.Trim(), null);
        return (parsed.Body.Trim(), globs);
    }

    /// <summary>
    /// The paths a file <c>@</c>-imports, resolved against its own directory.
    /// Fenced and inline code are skipped, as is anything inside an HTML
    /// comment, so an example in a code block is not an instruction.
    /// </summary>
    internal static IReadOnlyList<string> ExtractImports(string content, string filePath)
    {
        if (!content.Contains('@'))
            return [];

        var scannable = InstructionMarkdown.Scannable(content);
        if (!scannable.Contains('@'))
            return [];

        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (directory is not { Length: > 0 })
            return [];

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in ImportPattern.Matches(scannable))
        {
            var raw = match.Groups[1].Value;
            int fragment = raw.IndexOf('#');
            if (fragment >= 0)
                raw = raw[..fragment];
            raw = raw.Replace("\\ ", " ");
            if (raw.Length == 0 || !IsImportablePath(raw))
                continue;

            string resolved;
            try
            {
                resolved = Path.GetFullPath(ExpandHome(raw), directory);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (seen.Add(Canonical(resolved)))
                results.Add(resolved);
        }

        return results;
    }

    /// <summary>
    /// The reference's shape test on the text after an <c>@</c>: a relative or
    /// absolute path, or a bare name that starts with a path character. A second
    /// <c>@</c> or a run of punctuation is something else entirely.
    /// </summary>
    private static bool IsImportablePath(string candidate)
    {
        if (candidate.StartsWith("./", StringComparison.Ordinal) ||
            candidate.StartsWith("~/", StringComparison.Ordinal))
        {
            return true;
        }

        if (candidate.StartsWith('/'))
            return candidate.Length > 1;
        if (candidate.StartsWith('@'))
            return false;
        if (Regex.IsMatch(candidate, @"^[#%^&*()]+"))
            return false;
        return Regex.IsMatch(candidate, "^[a-zA-Z0-9._-]");
    }

    private static string ExpandHome(string path) =>
        path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path;

    // ------------------------------------------------------------- excludes

    /// <summary>
    /// The reference's <c>claudeMdExcludes</c>: glob patterns matched against the
    /// absolute path with forward slashes, honoured for the three tiers the user
    /// owns and never for a policy file.
    /// </summary>
    private static bool IsExcluded(InstructionScope scope, string path, InstructionMemoryType type)
    {
        if (type == InstructionMemoryType.Managed || scope.Excludes.Count == 0)
            return false;

        var candidate = Path.GetFullPath(path, scope.WorkingDirectory).Replace('\\', '/');
        foreach (var pattern in scope.Excludes)
        {
            if (pattern.Length == 0)
                continue;
            foreach (var expanded in ExpandBraces(pattern.Replace('\\', '/')))
            {
                if (Regex.IsMatch(candidate, ExcludeRegex(expanded), RegexOptions.IgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>Expands <c>{a,b}</c> alternations, which picomatch accepts.</summary>
    internal static IReadOnlyList<string> ExpandBraces(string pattern)
    {
        int open = pattern.IndexOf('{');
        if (open < 0)
            return [pattern];
        int depth = 0;
        for (int i = open; i < pattern.Length; i++)
        {
            if (pattern[i] == '{')
                depth++;
            else if (pattern[i] == '}' && --depth == 0)
            {
                var results = new List<string>();
                foreach (var option in SplitAlternatives(pattern[(open + 1)..i]))
                    results.AddRange(ExpandBraces(pattern[..open] + option + pattern[(i + 1)..]));
                return results;
            }
        }

        return [pattern];
    }

    private static IEnumerable<string> SplitAlternatives(string body)
    {
        int depth = 0, start = 0;
        for (int i = 0; i < body.Length; i++)
        {
            if (body[i] == '{')
                depth++;
            else if (body[i] == '}')
                depth--;
            else if (body[i] == ',' && depth == 0)
            {
                yield return body[start..i];
                start = i + 1;
            }
        }

        yield return body[start..];
    }

    private static string ExcludeRegex(string pattern)
    {
        var regex = new System.Text.StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '*')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    // "**/" also matches nothing at all, so a pattern rooted at
                    // "**/" still matches a file sitting at the root.
                    if (i + 2 < pattern.Length && pattern[i + 2] == '/')
                    {
                        regex.Append("(?:.*/)?");
                        i += 2;
                        continue;
                    }

                    regex.Append(".*");
                    i++;
                    continue;
                }

                regex.Append("[^/]*");
            }
            else if (c == '?')
            {
                regex.Append("[^/]");
            }
            else if (c == '[')
            {
                int close = pattern.IndexOf(']', i + 1);
                if (close < 0)
                {
                    regex.Append("\\[");
                    continue;
                }

                regex.Append(pattern[i..(close + 1)]);
                i = close;
            }
            else
            {
                regex.Append(Regex.Escape(c.ToString()));
            }
        }

        return regex.Append('$').ToString();
    }

    // ---------------------------------------------------------------- paths

    /// <summary>
    /// Every directory from the filesystem root down to
    /// <paramref name="directory"/>. The reference stops one short of the root
    /// itself and is bounded by nothing else — a repository is not the edge of
    /// where a user may keep instructions.
    /// </summary>
    private static IReadOnlyList<string> AncestorChain(string directory)
    {
        var chain = new List<string>();
        for (var current = new DirectoryInfo(Path.GetFullPath(directory));
             current is { Parent: not null };
             current = current.Parent)
        {
            chain.Add(current.FullName);
        }

        chain.Reverse();
        return chain;
    }

    /// <summary>
    /// Splits the directories a touched file implicates: those strictly below
    /// the working directory on the way to the file, and the working directory
    /// with its own ancestors (the reference's <c>Qmr</c>).
    /// </summary>
    private static (IReadOnlyList<string> Nested, IReadOnlyList<string> CwdLevel) TouchedDirectories(
        string workingDirectory, string filePath)
    {
        var root = Path.GetFullPath(workingDirectory);
        var nested = new List<string>();
        var start = Path.GetDirectoryName(Path.GetFullPath(filePath));
        for (var current = start is null ? null : new DirectoryInfo(start);
             current is { Parent: not null } && !PathsEqual(current.FullName, root);
             current = current.Parent)
        {
            if (IsUnder(current.FullName, root))
                nested.Add(current.FullName);
        }

        nested.Reverse();
        return (nested, AncestorChain(root));
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Canonical(a), Canonical(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> is inside <paramref name="root"/>, or is it.</summary>
    private static bool IsUnder(string path, string root)
    {
        try
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
            return relative != ".." &&
                   !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                   !Path.IsPathRooted(relative);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string Canonical(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>
    /// Carries the set of paths already loaded (so a file reached twice is read
    /// once) and the scope every step needs.
    /// </summary>
    private sealed class LoadState(InstructionScope scope)
    {
        public InstructionScope Scope { get; } = scope;

        public HashSet<string> ProcessedPaths { get; private set; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// A copy of this state that starts from the same processed paths. The
        /// reference walks a rules directory twice — once for the unconditional
        /// files and once for the conditional ones — and each walk has to see
        /// the files the other consumed.
        /// </summary>
        public LoadState ForkProcessedPaths() =>
            new(Scope) { ProcessedPaths = new HashSet<string>(ProcessedPaths, StringComparer.OrdinalIgnoreCase) };

        public void MergeProcessedPaths(LoadState other)
        {
            foreach (var path in other.ProcessedPaths)
                ProcessedPaths.Add(path);
        }
    }

    /// <summary>
    /// Where a linked worktree sits relative to the checkout it came from. When
    /// a worktree lives inside its main repository, the main repository's own
    /// directories are ancestors of the session without being part of it, and
    /// the reference does not read their project instructions.
    /// </summary>
    private sealed record WorktreeLayout(string? WorktreeRoot, string? MainRepoRoot)
    {
        private static readonly WorktreeLayout None = new(null, null);

        public static WorktreeLayout For(string workingDirectory)
        {
            try
            {
                for (var current = new DirectoryInfo(Path.GetFullPath(workingDirectory));
                     current is not null;
                     current = current.Parent)
                {
                    var marker = Path.Combine(current.FullName, ".git");
                    if (Directory.Exists(marker))
                        return None;
                    if (!File.Exists(marker))
                        continue;

                    // A linked worktree's .git is a file naming the main
                    // repository's own .git/worktrees/<name> directory.
                    var line = File.ReadAllText(marker).Trim();
                    const string prefix = "gitdir:";
                    if (!line.StartsWith(prefix, StringComparison.Ordinal))
                        return None;
                    var gitDir = line[prefix.Length..].Trim();
                    var worktreesDir = Path.GetDirectoryName(gitDir);
                    var dotGit = Path.GetDirectoryName(worktreesDir);
                    if (Path.GetFileName(worktreesDir) != "worktrees" || dotGit is null)
                        return None;
                    var main = Path.GetDirectoryName(dotGit);
                    if (main is not { Length: > 0 } || !IsUnder(current.FullName, main))
                        return None;
                    return new WorktreeLayout(current.FullName, main);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or ArgumentException or System.Security.SecurityException)
            {
                // A repository this loader cannot read is simply not a worktree.
            }

            return None;
        }

        /// <summary>
        /// True when the directory belongs to the main checkout and not to the
        /// worktree the session is in.
        /// </summary>
        public bool IsMainRepoOnly(string directory) =>
            MainRepoRoot is { Length: > 0 } main && WorktreeRoot is { Length: > 0 } worktree &&
            IsUnder(directory, main) && !IsUnder(directory, worktree);
    }
}
