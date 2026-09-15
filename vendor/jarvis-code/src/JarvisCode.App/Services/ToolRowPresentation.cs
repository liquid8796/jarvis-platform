using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>
/// English verb morphology for tool descriptions, ported from the reference
/// desktop app's transcript renderer: a description like "Install deps" reads
/// "Installing deps" while the call runs, "Installed deps" once it is done and
/// "Failed to install deps" when it errors. Only the first word (and verbs after
/// "and"/"then") are conjugated, and only when they are recognizably verbs — the
/// reference deliberately leaves anything ambiguous alone, so we do too.
/// </summary>
public static class VerbMorphology
{
    public sealed record Result(string Running, string Done, string Infinitive);

    // The reference's irregular past-tense map, verbatim.
    private static readonly Dictionary<string, string> IrregularPast = new(StringComparer.Ordinal)
    {
        ["begin"] = "began", ["bind"] = "bound", ["bring"] = "brought", ["build"] = "built",
        ["buy"] = "bought", ["catch"] = "caught", ["choose"] = "chose", ["come"] = "came",
        ["cut"] = "cut", ["debug"] = "debugged", ["dig"] = "dug", ["do"] = "did",
        ["draw"] = "drew", ["feed"] = "fed", ["feel"] = "felt", ["fight"] = "fought",
        ["find"] = "found", ["fly"] = "flew", ["forget"] = "forgot", ["freeze"] = "froze",
        ["get"] = "got", ["give"] = "gave", ["go"] = "went", ["have"] = "had",
        ["hide"] = "hid", ["hit"] = "hit", ["hold"] = "held", ["input"] = "input",
        ["keep"] = "kept", ["know"] = "knew", ["lead"] = "led", ["leave"] = "left",
        ["let"] = "let", ["lose"] = "lost", ["make"] = "made", ["mean"] = "meant",
        ["meet"] = "met", ["override"] = "overrode", ["overwrite"] = "overwrote", ["pay"] = "paid",
        ["put"] = "put", ["quit"] = "quit", ["read"] = "read", ["rebuild"] = "rebuilt",
        ["redo"] = "redid", ["rerun"] = "reran", ["reset"] = "reset", ["rewrite"] = "rewrote",
        ["run"] = "ran", ["see"] = "saw", ["seek"] = "sought", ["send"] = "sent",
        ["set"] = "set", ["show"] = "showed", ["shut"] = "shut", ["sit"] = "sat",
        ["sleep"] = "slept", ["spend"] = "spent", ["spin"] = "spun", ["split"] = "split",
        ["spread"] = "spread", ["stand"] = "stood", ["sweep"] = "swept", ["sync"] = "synced",
        ["take"] = "took", ["teach"] = "taught", ["tear"] = "tore", ["tell"] = "told",
        ["think"] = "thought", ["throw"] = "threw", ["understand"] = "understood", ["undo"] = "undid",
        ["unset"] = "unset", ["win"] = "won", ["unwrap"] = "unwrapped", ["unzip"] = "unzipped",
        ["write"] = "wrote",
    };

    // Irregular gerunds (consonant doubling and friends), verbatim from the reference.
    private static readonly Dictionary<string, string> IrregularGerund = new(StringComparer.Ordinal)
    {
        ["begin"] = "beginning", ["commit"] = "committing", ["control"] = "controlling",
        ["debug"] = "debugging", ["emit"] = "emitting", ["equip"] = "equipping",
        ["forget"] = "forgetting", ["format"] = "formatting", ["input"] = "inputting",
        ["occur"] = "occurring", ["omit"] = "omitting", ["output"] = "outputting",
        ["permit"] = "permitting", ["prefer"] = "preferring", ["quit"] = "quitting",
        ["refer"] = "referring", ["rerun"] = "rerunning", ["reset"] = "resetting",
        ["screenshot"] = "screenshotting", ["snapshot"] = "snapshotting", ["submit"] = "submitting",
        ["sync"] = "syncing", ["transfer"] = "transferring", ["unset"] = "unsetting",
        ["unwrap"] = "unwrapping", ["unzip"] = "unzipping",
    };

    // Regular verbs the reference recognizes as safe to conjugate.
    private static readonly HashSet<string> RegularVerbs = new(StringComparer.Ordinal)
    {
        "add", "analyze", "append", "apply", "archive", "assert", "attempt", "audit", "autofix",
        "await", "benchmark", "bisect", "call", "capture", "check", "click", "clone", "collect",
        "compare", "compile", "compute", "confirm", "connect", "convert", "copy", "count", "create",
        "curl", "decode", "delete", "deploy", "detect", "disable", "discard", "dismiss", "display",
        "download", "dump", "edit", "emit", "enable", "encode", "ensure", "enumerate", "evaluate",
        "execute", "expand", "expect", "export", "extract", "fetch", "fill", "filter", "fix",
        "flush", "focus", "follow", "generate", "grep", "identify", "ignore", "import", "include",
        "inject", "insert", "inspect", "install", "invoke", "kill", "launch", "lint", "list",
        "load", "locate", "loop", "measure", "merge", "monitor", "move", "navigate", "normalize",
        "parse", "patch", "pick", "ping", "pipe", "poll", "post", "prepare", "preview", "print",
        "probe", "profile", "prune", "publish", "pull", "push", "queue", "rebase", "recheck",
        "record", "recover", "redirect", "reduce", "refresh", "regenerate", "reinstall", "relaunch",
        "reload", "remove", "rename", "render", "reopen", "repeat", "replay", "reply", "report",
        "request", "resolve", "restart", "restore", "retry", "revert", "save", "scan", "screenshot",
        "scroll", "search", "select", "serialize", "serve", "settle", "skip", "snapshot", "sort",
        "spawn", "squash", "stage", "start", "stash", "stop", "strip", "summarize", "switch",
        "sync", "tail", "tally", "test", "toggle", "touch", "trace", "track", "trigger", "trim",
        "truncate", "try", "typecheck", "unblock", "uninstall", "unlink", "unmount", "unpack",
        "update", "upgrade", "upload", "validate", "verify", "visit", "wait", "walk", "warn", "wipe",
    };

    /// <summary>Every conjugatable verb: irregulars plus the regular list.</summary>
    private static readonly HashSet<string> AllVerbs = BuildAllVerbs();

    // Past forms and participles that must never be mistaken for a base verb.
    private static readonly HashSet<string> NonVerbForms = BuildNonVerbForms();

    // Verbs whose past forms read oddly mid-sentence; excluded from "and X" continuation
    // conjugation only (they still conjugate as the leading word).
    private static readonly HashSet<string> ContinuationExcluded = new(StringComparer.Ordinal)
    {
        "output", "input", "lead", "feed", "spread", "set", "cut", "split", "hit", "let", "put",
        "quit", "shut", "read", "go", "make", "dig", "tear", "win", "fly", "spin", "control",
        "permit", "have", "hold", "keep", "mean", "feel", "do", "see", "know", "think", "understand",
    };

    private static readonly Regex HasVowel = new("[aeiou]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CvcEnding = new("[^aeiou][aeiou][bcdfghjklmnpqrstvz]$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PrefixedVerb = new("^(re|un|de|pre|co|sub|over|out|mis|auto|post)-(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FirstToken = new(@"^(\S+)([\s\S]*)$", RegexOptions.Compiled);

    // "… and verify the output" / "then run tests" — the connector plus a lowercase verb.
    private static readonly Regex Continuation = new(
        @"(?<![\w-])((?:(?:[Aa]nd|[Tt]hen)\s+)+)([a-z]{2,16})(?=$|[\s,;!?]|\.(?:\s|$))",
        RegexOptions.Compiled);

    // The continuation is a clause subject ("and the test passes"), not an imperative.
    private static readonly Regex ClauseGuard = new(
        @"^(?:\s+\w+){0,2}\s+(?:is|are|was|were|has|have|do|does|did|will|would|should|can|could|passes|passed|fails|failed|exists|works|worked|succeeds|succeeded|stays|remains|looks|runs|ran|not|if|when|unless|whether)(?:n['’]t)?\b",
        RegexOptions.Compiled);

    private static HashSet<string> BuildAllVerbs()
    {
        var set = new HashSet<string>(RegularVerbs, StringComparer.Ordinal);
        set.UnionWith(IrregularPast.Keys);
        set.UnionWith(IrregularGerund.Keys);
        return set;
    }

    private static HashSet<string> BuildNonVerbForms()
    {
        var set = new HashSet<string>(StringComparer.Ordinal)
        {
            "been", "broken", "chosen", "done", "drawn", "driven", "eaten", "fallen", "forgotten",
            "given", "gone", "grown", "hidden", "known", "ridden", "risen", "seen", "shown",
            "spoken", "taken", "thrown", "torn", "worn", "written",
        };
        foreach (var (verb, past) in IrregularPast)
        {
            if (verb != past)
            {
                set.Add(past);
            }
        }

        return set;
    }

    /// <summary>
    /// Conjugates a description's leading verb (and "and …" continuations) into
    /// running/done/infinitive forms; null when the first word is not a verb the
    /// reference would touch.
    /// </summary>
    public static Result? Morph(string description)
    {
        var trimmed = description.TrimStart();
        var leading = description[..(description.Length - trimmed.Length)];
        var match = FirstToken.Match(trimmed);
        if (!match.Success)
        {
            return null;
        }

        var word = match.Groups[1].Value;
        var rest = match.Groups[2].Value;

        var prefixed = PrefixedVerb.Match(word);
        if (prefixed.Success && IsVerbWord(prefixed.Groups[2].Value))
        {
            var prefix = prefixed.Groups[1].Value;
            var inner = prefixed.Groups[2].Value;
            var combined = (prefix + inner).ToLowerInvariant();
            var innerLower = inner.ToLowerInvariant();
            if (NonVerbForms.Contains(combined) || NonVerbForms.Contains(innerLower))
            {
                return null;
            }

            if (!AllVerbs.Contains(combined) && !AllVerbs.Contains(innerLower))
            {
                return null;
            }

            string Conjugate(Dictionary<string, string> map, Func<string, string> fallback) =>
                prefix + "-" + PreserveCase(inner, map.TryGetValue(combined, out var mapped)
                    ? mapped[prefix.Length..]
                    : fallback(inner));

            return new Result(
                Running: leading + Conjugate(IrregularGerund, Gerund) + MorphContinuations(rest, Gerund),
                Done: leading + Conjugate(IrregularPast, Past) + MorphContinuations(rest, Past),
                Infinitive: leading + word.ToLowerInvariant() + rest);
        }

        if (!IsVerbWord(word) || !AllVerbs.Contains(word.ToLowerInvariant()))
        {
            return null;
        }

        return new Result(
            Running: leading + PreserveCase(word, Gerund(word)) + MorphContinuations(rest, Gerund),
            Done: leading + PreserveCase(word, Past(word)) + MorphContinuations(rest, Past),
            Infinitive: leading + char.ToLowerInvariant(word[0]) + word[1..] + rest);
    }

    /// <summary>"install" → "installing", with the reference's spelling rules.</summary>
    public static string Gerund(string word)
    {
        var lower = word.ToLowerInvariant();
        if (IrregularGerund.TryGetValue(lower, out var irregular))
        {
            return irregular;
        }

        if (Regex.IsMatch(word, "ie$", RegexOptions.IgnoreCase))
        {
            return word[..^2] + "ying";
        }

        if (Regex.IsMatch(word, "[^eoy]e$", RegexOptions.IgnoreCase))
        {
            return word[..^1] + "ing";
        }

        if (CvcEnding.IsMatch(word) && VowelCount(word) == 1)
        {
            return word + word[^1] + "ing";
        }

        if (Regex.IsMatch(word, "c$", RegexOptions.IgnoreCase))
        {
            return word + "king";
        }

        return word + "ing";
    }

    /// <summary>"install" → "installed", with the reference's spelling rules.</summary>
    public static string Past(string word)
    {
        var lower = word.ToLowerInvariant();
        if (IrregularPast.TryGetValue(lower, out var irregular))
        {
            return irregular;
        }

        if (Regex.IsMatch(word, "e$", RegexOptions.IgnoreCase))
        {
            return word + "d";
        }

        if (Regex.IsMatch(word, "[^aeiou]y$", RegexOptions.IgnoreCase))
        {
            return word[..^1] + "ied";
        }

        if (CvcEnding.IsMatch(word) && VowelCount(word) == 1)
        {
            return word + word[^1] + "ed";
        }

        if (Regex.IsMatch(word, "c$", RegexOptions.IgnoreCase))
        {
            return word + "ked";
        }

        if (IrregularGerund.TryGetValue(lower, out var gerund))
        {
            return gerund[..^3] + "ed";
        }

        return word + "ed";
    }

    private static string MorphContinuations(string text, Func<string, string> conjugate)
        => Continuation.Replace(text, m =>
        {
            var verb = m.Groups[2].Value;
            if (!AllVerbs.Contains(verb) || ContinuationExcluded.Contains(verb) || NonVerbForms.Contains(verb))
            {
                return m.Value;
            }

            var after = text[(m.Index + m.Length)..];
            return ClauseGuard.IsMatch(after) ? m.Value : m.Groups[1].Value + conjugate(verb);
        });

    private static string PreserveCase(string original, string replacement)
        => char.IsUpper(original[0]) ? char.ToUpperInvariant(replacement[0]) + replacement[1..] : replacement;

    private static int VowelCount(string word) => word.Count(c => "aeiouAEIOU".Contains(c));

    /// <summary>The reference's "is this plausibly a base verb" gate.</summary>
    public static bool IsVerbWord(string word)
    {
        if (word.Length is < 2 or > 16 || !word.All(char.IsAsciiLetter) || word == word.ToUpperInvariant())
        {
            return false;
        }

        var lower = word.ToLowerInvariant();
        if (NonVerbForms.Contains(lower))
        {
            return false;
        }

        if (IrregularPast.ContainsKey(lower))
        {
            return true;
        }

        if (Regex.IsMatch(word, "ing$", RegexOptions.IgnoreCase) && word.Length > 4)
        {
            return false;
        }

        if (Regex.IsMatch(word, "ed$", RegexOptions.IgnoreCase) &&
            !Regex.IsMatch(word, "eed$", RegexOptions.IgnoreCase) && word.Length > 3)
        {
            return false;
        }

        if (Regex.IsMatch(word, "s$", RegexOptions.IgnoreCase) &&
            !Regex.IsMatch(word, "(ss|us)$", RegexOptions.IgnoreCase) && word.Length > 3)
        {
            return false;
        }

        return HasVowel.IsMatch(word) || word.Contains('y', StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Recognizes a finished shell call as a git operation the way the reference
/// transcript does — commit/push/merge/rebase and gh PR calls get their own verbs
/// ("Committed abc1234", "Created PR #12" with the PR linked) instead of "Ran".
/// The reference reads a structured gitOperation off its harness result; our
/// engine has none, so the same facts are recovered from the command line and
/// the tool's output.
/// </summary>
public static class GitShellSummary
{
    public sealed record Info(string Verb, string RunningVerb, string? Meta, bool MetaIsCode, string? Url);

    private static readonly Regex CommitSha = new(@"\[[^\r\n\[\]]+\s([0-9a-f]{7,40})\]", RegexOptions.Compiled);
    private static readonly Regex PrUrl = new(@"https://github\.com/[^\s/]+/[^\s/]+/pull/(\d+)", RegexOptions.Compiled);
    private static readonly Regex PushBranch = new(@"^\s*(?:[0-9a-f]+\.\.+[0-9a-f]+|\*\s+\[new branch\])\s+(\S+)\s*->", RegexOptions.Compiled | RegexOptions.Multiline);

    public static Info? TryDetect(string? command, string? output)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        output ??= "";

        if (HasWord(command, "gh pr create"))
        {
            return Pr("Created PR", "Creating PR", output);
        }

        if (HasWord(command, "gh pr merge"))
        {
            return Pr("Merged PR", "Merging PR", output);
        }

        if (HasWord(command, "gh pr edit"))
        {
            return Pr("Edited PR", "Editing PR", output);
        }

        if (HasWord(command, "git cherry-pick"))
        {
            var sha = CommitSha.Match(output);
            return new Info("Cherry-picked", "Cherry-picking", sha.Success ? sha.Groups[1].Value[..7] : null, MetaIsCode: true, Url: null);
        }

        if (HasWord(command, "git commit"))
        {
            var sha = CommitSha.Match(output);
            if (!sha.Success)
            {
                return null; // No commit landed (hook failure, nothing staged) — keep the generic row.
            }

            var amended = command.Contains("--amend", StringComparison.Ordinal);
            return new Info(
                amended ? "Amended commit" : "Committed",
                amended ? "Amending commit" : "Committing",
                sha.Groups[1].Value[..7], MetaIsCode: true, Url: null);
        }

        if (HasWord(command, "git push"))
        {
            // The reference records a push only when a branch actually moved.
            var branch = PushBranch.Match(output);
            return branch.Success
                ? new Info("Pushed", "Pushing", branch.Groups[1].Value, MetaIsCode: true, Url: null)
                : null;
        }

        if (HasWord(command, "git merge"))
        {
            return RefTarget(command, "git merge", "Merged", "Merging");
        }

        if (HasWord(command, "git rebase"))
        {
            return RefTarget(command, "git rebase", "Rebased onto", "Rebasing onto");
        }

        return null;
    }

    private static Info? Pr(string verb, string runningVerb, string output)
    {
        var url = PrUrl.Match(output);
        return url.Success
            ? new Info(verb, runningVerb, $"#{url.Groups[1].Value}", MetaIsCode: true, url.Value)
            : new Info(verb, runningVerb, null, MetaIsCode: false, Url: null);
    }

    private static Info? RefTarget(string command, string prefix, string verb, string runningVerb)
    {
        var index = command.IndexOf(prefix, StringComparison.Ordinal);
        var args = command[(index + prefix.Length)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var target = args.FirstOrDefault(a => !a.StartsWith('-') && !a.Contains('&') && !a.Contains('|'));
        return target is null ? null : new Info(verb, runningVerb, target, MetaIsCode: true, Url: null);
    }

    private static bool HasWord(string command, string phrase)
    {
        var index = command.IndexOf(phrase, StringComparison.Ordinal);
        if (index < 0)
        {
            return false;
        }

        // "git commitx" or "mygit commit" don't count.
        var before = index == 0 ? ' ' : command[index - 1];
        var afterIndex = index + phrase.Length;
        var after = afterIndex >= command.Length ? ' ' : command[afterIndex];
        return !char.IsLetterOrDigit(before) && before != '-' && !char.IsLetterOrDigit(after) && after != '-';
    }
}

/// <summary>What an expanded tool row's body renders as, per the reference.</summary>
public enum ToolBodyKind
{
    /// <summary>Arguments as JSON plus the raw result — the fallback.</summary>
    Default,

    /// <summary>A prompt-prefixed command line plus terminal output (shell).</summary>
    Command,

    /// <summary>A line diff of the change (Edit / Write / NotebookEdit).</summary>
    Diff,

    /// <summary>File content, syntax highlighted by extension (Read).</summary>
    FileView,

    /// <summary>The todo list as a checklist (todo_write).</summary>
    Todos,
}

/// <summary>How a tool row reads in each state, reference-style.</summary>
public sealed record ToolRowInfo(
    string Verb,
    string RunningVerb,
    string? FailedVerb,
    string? Meta,
    bool MetaIsCode,
    string? MetaHref,
    string? RunningLabel,
    string? DoneLabel,
    string? FailedLabel,
    ToolBodyKind Kind)
{
    /// <summary>A row described only by the model's call description (unmapped tools).</summary>
    public static ToolRowInfo Fallback(string description) =>
        new(description, description, null, null, false, null, null, null, null, ToolBodyKind.Default);
}

/// <summary>
/// Builds the reference desktop's per-tool row wording for our tool set:
/// "Read {file}", "Edited {file}", "Searched {pattern}", git-aware shell verbs,
/// and conjugated descriptions for shell and Agent.
/// </summary>
public static class ToolRowPresentation
{
    public static ToolRowInfo Describe(string toolName, JsonObject? args, string? resultText, string fallbackDescription)
    {
        string? Arg(string name) => args?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        string? Trimmed(string name) => Arg(name) is { } s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

        // A tool from one of the desktop shell's own servers is rendered by what
        // it does, not by the server it was filed under — the reference's own
        // prefix normalisation. Tools from a configured MCP server keep their
        // full name and fall through to the "Used {server}: {tool}" branch.
        var wireName = toolName;
        if (InternalMcpServers.IsInternal(toolName))
        {
            toolName = InternalMcpServers.ShortName(toolName);
        }

        switch (toolName)
        {
            case "PowerShell":
            {
                var command = OneLine(Arg("command"));
                // Git verbs need the command's output — a running call stays generic,
                // like the reference (its gitOperation exists only on the result).
                var git = resultText is null ? null : GitShellSummary.TryDetect(Arg("command"), resultText);
                if (git is not null)
                {
                    return new ToolRowInfo(git.Verb, git.RunningVerb, null,
                        git.Meta ?? command, git.Meta is not null && git.MetaIsCode, git.Url,
                        null, null, null, ToolBodyKind.Command);
                }

                var description = Trimmed("description");
                var morph = description is null ? null : VerbMorphology.Morph(description);
                return new ToolRowInfo(
                    "Ran", "Running", "Failed to run",
                    description ?? command ?? "a command", MetaIsCode: description is null && command is not null, null,
                    morph?.Running ?? description,
                    morph?.Done ?? description,
                    morph is null ? null : $"Failed to {morph.Infinitive}",
                    ToolBodyKind.Command);
            }

            case "Read":
                return new ToolRowInfo("Read", "Reading", "Failed to read",
                    FileMeta(Arg("file_path")), false, null, null, null, null, ToolBodyKind.FileView);

            case "list_directory":
                return new ToolRowInfo("Listed", "Listing", "Failed to list",
                    ShortPath(Arg("path")), false, null, null, null, null, ToolBodyKind.Default);

            case "Write":
            {
                // The tool reports "Overwrote {path}…" when the file existed.
                var updated = resultText?.StartsWith("Overwrote", StringComparison.Ordinal) == true;
                return new ToolRowInfo(
                    updated ? "Updated" : "Created", updated ? "Updating" : "Creating", "Failed to write",
                    FileMeta(Arg("file_path")), false, null, null, null, null, ToolBodyKind.Diff);
            }

            case "Edit":
                return new ToolRowInfo("Edited", "Editing", "Failed to edit",
                    FileMeta(Arg("file_path")), false, null, null, null, null, ToolBodyKind.Diff);

            case "NotebookEdit":
                return new ToolRowInfo("Edited", "Editing", "Failed to edit",
                    FileMeta(Arg("notebook_path")), false, null, null, null, null, ToolBodyKind.Diff);

            case "Glob":
            case "Grep":
                return new ToolRowInfo("Searched", "Searching", "Failed to search",
                    Arg("pattern"), true, null, null, null, null, ToolBodyKind.Default);

            case "WebFetch":
                return new ToolRowInfo("Fetched", "Fetching", "Failed to fetch",
                    Arg("url"), false, null, null, null, null, ToolBodyKind.Default);

            case "WebSearch":
                return new ToolRowInfo("Searched web", "Searching web", "Failed to search web",
                    Arg("query"), false, null, null, null, null, ToolBodyKind.Default);

            case "Agent":
            {
                var description = Trimmed("description") ?? FirstLine(Arg("prompt"));
                var morph = description is null ? null : VerbMorphology.Morph(description);
                return new ToolRowInfo(
                    "Ran agent", "Running agent", "Failed to run agent",
                    description, false, null,
                    morph?.Running ?? description,
                    morph?.Done ?? description,
                    morph is null ? null : $"Failed to {morph.Infinitive}",
                    ToolBodyKind.Default);
            }

            case "Skill":
            {
                var skill = Trimmed("Skill") ?? Trimmed("name");
                return new ToolRowInfo("Ran skill", "Running skill", "Failed to run skill",
                    skill is null ? null : $"/{skill}", true, null, null, null, null, ToolBodyKind.Default);
            }

            case "todo_write":
            {
                var cleared = args?["todos"] is not JsonArray { Count: > 0 };
                return new ToolRowInfo(cleared ? "Cleared todos" : "Updated todos", "Updating todos", null,
                    null, false, null, null, null, null, ToolBodyKind.Todos);
            }

            case "ExitPlanMode":
                return new ToolRowInfo("Proposed plan", "Proposing plan", "Failed to propose plan",
                    null, false, null, null, null, null, ToolBodyKind.Default);

            case "EnterPlanMode":
                return new ToolRowInfo("Started planning", "Making a plan", "Failed to make a plan",
                    null, false, null, null, null, null, ToolBodyKind.Default);

            case "AskUserQuestion":
                return new ToolRowInfo("Asked a question", "Asking a question", "Failed to ask a question",
                    null, false, null, null, null, null, ToolBodyKind.Default);

            case "computer":
                return ComputerRow(Arg("action"), fallbackDescription);

            case "computer_batch":
            {
                // A one-action batch reads better as that action; the reference's
                // own row falls back to "Batch — N actions" for anything longer.
                var batch = args?["actions"] as JsonArray;
                if (batch is { Count: 1 }
                    && batch[0] is JsonObject only
                    && only["action"] is JsonValue actionValue
                    && actionValue.TryGetValue<string>(out var single))
                {
                    return ComputerRow(single, fallbackDescription);
                }

                var count = batch?.Count ?? 0;
                var label = $"Batch — {count} action{(count == 1 ? "" : "s")}";
                return new ToolRowInfo(label, label, null,
                    null, false, null, null, null, null, ToolBodyKind.Default);
            }

            case "screenshot":
                return new ToolRowInfo("Took screenshot", "Taking screenshot", "Failed to take screenshot",
                    null, false, null, null, null, null, ToolBodyKind.Default);

            default:
                if (wireName.StartsWith("mcp__", StringComparison.Ordinal))
                {
                    var label = McpLabel(wireName);
                    return new ToolRowInfo($"Used {label}", $"Using {label}", null,
                        null, false, null, null, null, null, ToolBodyKind.Default);
                }

                return ToolRowInfo.Fallback(fallbackDescription);
        }
    }

    private static ToolRowInfo ComputerRow(string? action, string fallbackDescription)
    {
        var (done, running) = action switch
        {
            "screenshot" => ("Took screenshot", "Taking screenshot"),
            "left_click" or "right_click" or "double_click" or "triple_click" or "middle_click" => ("Clicked", "Clicking"),
            "left_click_drag" => ("Dragged", "Dragging"),
            "hover" or "mouse_move" => ("Moved mouse", "Moving mouse"),
            "type" => ("Typed", "Typing"),
            "key" => ("Pressed key", "Pressing key"),
            "scroll" or "scroll_to" => ("Scrolled", "Scrolling"),
            null => (fallbackDescription, fallbackDescription),
            _ => (Humanize(action), Humanize(action)),
        };
        return new ToolRowInfo(done, running, null, null, false, null, null, null, null, ToolBodyKind.Default);

        static string Humanize(string action) =>
            char.ToUpperInvariant(action[0]) + action[1..].Replace('_', ' ');
    }

    /// <summary>"mcp__server__do_thing" → "server: do thing", like the reference.</summary>
    public static string McpLabel(string toolName)
    {
        var parts = toolName.Split("__", StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3
            ? $"{parts[1]}: {string.Join("__", parts[2..]).Replace('_', ' ')}"
            : (parts.LastOrDefault() ?? toolName).Replace('_', ' ');
    }

    /// <summary>The reference row shows the basename, never the whole path.</summary>
    public static string? FileMeta(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.TrimEnd('/', '\\');
        var slash = trimmed.LastIndexOfAny(['/', '\\']);
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    /// <summary>Directories keep the last two segments for context ("src/Views").</summary>
    public static string? ShortPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var parts = path.Replace('\\', '/').TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 2 ? string.Join("/", parts[^2..]) : string.Join("/", parts);
    }

    /// <summary>"Read a file (919–1058)" — the range a read call covered, from offset/limit.</summary>
    public static string? ReadRange(JsonObject? args)
    {
        var offset = args?["offset"] is JsonValue o && o.TryGetValue<int>(out var off) ? off : (int?)null;
        var limit = args?["limit"] is JsonValue l && l.TryGetValue<int>(out var lim) ? lim : (int?)null;
        if (offset is null && limit is null)
        {
            return null;
        }

        var start = offset ?? 1;
        return limit is null ? $"{start}–" : $"{start}–{start + limit.Value - 1}";
    }

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var line = text.AsSpan().Trim();
        var newline = line.IndexOfAny('\r', '\n');
        if (newline >= 0)
        {
            line = line[..newline].TrimEnd();
        }

        return line.Length > 64 ? string.Concat(line[..64], "…") : line.ToString();
    }

    private static string? OneLine(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var line = command.Trim().Replace("\r", " ").Replace("\n", " ");
        return line.Length > 120 ? line[..120] + "…" : line;
    }
}
