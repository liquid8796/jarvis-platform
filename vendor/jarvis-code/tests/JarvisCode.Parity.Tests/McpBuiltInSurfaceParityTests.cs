using System.Text;
using System.Text.RegularExpressions;
using JarvisCode.Core.Mcp;
using Xunit;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// The CLI's own built-in-MCP surface, read out of the installed binary.
///
/// The desktop's ten in-process servers are checked by
/// <see cref="InternalMcpSurfaceParityTests"/>; this is the other half. The CLI
/// hosts none of those servers — they reach it over its <c>sdk</c> transport —
/// but it carries the authoritative list of their names, and a rule or two that
/// keys off it. Both are extracted here rather than pinned as a table, so a
/// reference release that adds a name lands as one failure naming it.
/// </summary>
public sealed class McpBuiltInSurfaceParityTests
{
    /// <summary>
    /// Reads the string array or Set the anchor sits inside: back to the opening
    /// bracket, forward to the closing one, then every quoted element.
    /// </summary>
    private static IReadOnlyList<string> ReadArray(ReferenceCorpus corpus, string anchor)
    {
        foreach (var offset in corpus.Occurrences(anchor, limit: 32))
        {
            var window = corpus.Window(offset, before: 400, after: 900);
            var at = window.IndexOf(anchor, StringComparison.Ordinal);
            var open = window.LastIndexOf('[', at);
            var close = window.IndexOf(']', at);
            if (open < 0 || close < 0)
            {
                continue;
            }

            var body = window[(open + 1)..close];
            var names = new List<string>();
            foreach (var part in body.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
                {
                    names.Add(trimmed[1..^1]);
                }
            }

            if (names.Count > 0)
            {
                return names;
            }
        }

        return [];
    }

    private static void AssertSameSet(
        string what, IReadOnlyList<string> reference, IEnumerable<string> ours)
    {
        Assert.True(reference.Count > 0, $"{what}: could not be read out of the installed CLI.");
        var mine = ours.ToHashSet(StringComparer.Ordinal);
        var theirs = reference.ToHashSet(StringComparer.Ordinal);
        var missing = theirs.Except(mine).Order(StringComparer.Ordinal).ToList();
        var extra = mine.Except(theirs).Order(StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0 && extra.Count == 0,
            new StringBuilder()
                .AppendLine($"{what} disagrees with CLI {ReferenceInstall.CliVersion ?? "unknown"}:")
                .AppendLine(missing.Count == 0 ? "" : "  in the reference, not in this build: " + string.Join(", ", missing))
                .AppendLine(extra.Count == 0 ? "" : "  in this build, not in the reference: " + string.Join(", ", extra))
                .ToString());
    }

    [ReferenceCliFact]
    public void Prefix_matched_built_in_server_names_are_the_reference_s() =>
        AssertSameSet(
            "The prefix half of the built-in server inventory",
            ReadArray(ReferenceCorpora.Cli, "\"claude_in_chrome\",\"claude_browser\""),
            McpBuiltInServers.PrefixMatchedNames);

    [ReferenceCliFact]
    public void Exactly_matched_built_in_server_names_are_the_reference_s() =>
        AssertSameSet(
            "The exact half of the built-in server inventory",
            ReadArray(ReferenceCorpora.Cli, "\"workspace\",\"terminal\",\"office\""),
            McpBuiltInServers.ExactlyMatchedNames);

    [ReferenceCliFact]
    public void Unconditionally_internal_server_names_are_the_reference_s() =>
        AssertSameSet(
            "The unconditionally-internal server names",
            ReadArray(ReferenceCorpora.Cli, "\"ide\",\"remote-devices\""),
            McpBuiltInServers.UnconditionallyInternalNames);

    [ReferenceCliFact]
    public void The_two_ide_tools_the_model_may_see_are_the_reference_s() =>
        AssertSameSet(
            "The ide tools the model may see",
            ReadArray(ReferenceCorpora.Cli, "\"mcp__ide__executeCode\",\"mcp__ide__getDiagnostics\""),
            McpBuiltInServers.IdeModelVisibleTools);

    /// <summary>
    /// The two-minute threshold. It is a number rather than a sentence, so the
    /// prose check cannot see it; the anchor is the declaration it sits in.
    /// </summary>
    [ReferenceCliFact]
    public void The_auto_background_delay_is_the_reference_s()
    {
        // The two are declared together — `var K=120000,V=new Set([...])` in
        // 2.1.251, `var V=120000,W=new Set([...])` in 2.1.257 — so the check reads
        // the bytes just ahead of the transport set rather than pinning the
        // minified names, which a build re-letters.
        const string transports = "=new Set([\"sse-ide\",\"ws-ide\"])";
        var declared = ReferenceCorpora.Cli.Occurrences(transports)
            .Select(offset => ReferenceCorpora.Cli.Window(offset, 24, 0))
            .Any(window => window.Contains($"={McpAutoBackground.DefaultDelayMs},", StringComparison.Ordinal));
        Assert.True(
            declared,
            $"CLI {ReferenceInstall.CliVersion ?? "unknown"} no longer declares the MCP auto-background " +
            $"delay as {McpAutoBackground.DefaultDelayMs}ms beside its two exempt transports.");
    }

    /// <summary>
    /// The clamp on the environment override: the reference reads the env var and
    /// returns <c>Math.min(Math.max(0,x),NAME)</c>, where NAME is declared as
    /// 2147483647 - the largest delay a JavaScript timer takes.
    ///
    /// <para>
    /// NAME is re-derived from the clamp rather than remembered: it is a minified
    /// local, and 2.1.260 re-lettered it from vb to ob without moving anything
    /// this port depends on. A check that pins the letter fails on a rename and
    /// says nothing about the rule.
    /// </para>
    /// </summary>
    [ReferenceCliFact]
    public void The_auto_background_clamp_is_the_timer_maximum()
    {
        Assert.Equal(2_147_483_647, McpAutoBackground.MaxDelayMs);

        const string envVar = "CLAUDE_CODE_MCP_AUTO_BACKGROUND_MS";
        var clamp = new Regex(@"Math\.min\(Math\.max\(0,\w+\),(\w+)\)");
        var names = ReferenceCorpora.Cli.Occurrences(envVar)
            .Select(offset => ReferenceCorpora.Cli.Window(offset, 0, 200))
            .Select(window => clamp.Match(window))
            .Where(static m => m.Success)
            .Select(static m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            names.Count > 0,
            $"CLI {ReferenceInstall.CliVersion ?? "unknown"} no longer clamps the auto-background override " +
            $"beside {envVar}.");
        Assert.True(
            names.Any(name =>
                ReferenceCorpora.Cli.Contains($"{name}={McpAutoBackground.MaxDelayMs};") ||
                ReferenceCorpora.Cli.Contains($"{name}={McpAutoBackground.MaxDelayMs},")),
            $"CLI {ReferenceInstall.CliVersion ?? "unknown"} clamps to {string.Join("/", names)}, and none of " +
            $"those is declared as {McpAutoBackground.MaxDelayMs}.");
    }

    /// <summary>
    /// The upload refusal, pinned here rather than by registering its whole file
    /// as ported prose: that file is mostly this port's own extension text, and
    /// registering it would file ten unrelated strings as deltas. Both fixed
    /// halves of the sentence are checked, so a reword on either side of the
    /// path lands as a failure.
    /// </summary>
    [ReferenceCliFact]
    public void The_upload_refusal_is_the_reference_s()
    {
        var refusal = JarvisCode.App.Services.BrowserFileUploadTool.OutOfScope("PATH");
        Assert.StartsWith("Cannot upload \"PATH\": ", refusal, StringComparison.Ordinal);
        Assert.True(
            ReferenceCorpora.Cli.Contains(
                "only files this session is allowed to read can be uploaded. Ask the user to share " +
                "the file with this session, or to add its folder with /add-dir."),
            $"CLI {ReferenceInstall.CliVersion ?? "unknown"} no longer refuses an out-of-scope upload " +
            "in these words.");
    }

    /// <summary>Both environment variables the threshold reads, by name.</summary>
    [ReferenceCliFact]
    public void The_auto_background_environment_variables_are_the_reference_s()
    {
        Assert.True(ReferenceCorpora.Cli.Contains("CLAUDE_CODE_MCP_AUTO_BACKGROUND_MS"));
        Assert.True(ReferenceCorpora.Cli.Contains("CLAUDE_AUTO_BACKGROUND_TASKS"));
    }
}
