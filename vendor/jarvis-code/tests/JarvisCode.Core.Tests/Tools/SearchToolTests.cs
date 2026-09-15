using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.Core.Tools;
using JarvisCode.Core.Tools.BuiltIn;

namespace JarvisCode.Core.Tests.Tools;

/// <summary>Covers what the search tools promise beyond finding a plain substring.</summary>
public sealed class SearchToolTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private ToolExecutionContext Context => new() { WorkingDirectory = _temp.Path };

    public void Dispose() => _temp.Dispose();

    private static JsonObject Args(params (string Key, JsonNode? Value)[] pairs)
    {
        var json = new JsonObject();
        foreach (var (key, value) in pairs)
            json[key] = value;
        return json;
    }

    private Task<ToolResult> Grep(params (string Key, JsonNode? Value)[] pairs) =>
        new GrepTool().ExecuteAsync(Args(pairs), Context, default);

    [Fact]
    public async Task GrepReadsUtf16FilesInsteadOfWritingThemOffAsBinary()
    {
        // Every ASCII character in UTF-16 carries a NUL byte, which the plain binary
        // heuristic used to read as "not text".
        File.WriteAllText(Path.Combine(_temp.Path, "wide.txt"), "alpha\nbeta\n", Encoding.Unicode);

        var result = await Grep(("pattern", "beta"), ("output_mode", "content"));

        Assert.False(result.IsError);
        Assert.Contains("wide.txt:2: beta", result.Content);
    }

    [Fact]
    public async Task GrepGlobWithoutASeparatorStillMatchesThatNameInAnyDirectory()
    {
        _temp.WriteFile("top.cs", "needle");
        _temp.WriteFile("src/deep/inner.cs", "needle");
        _temp.WriteFile("src/notes.txt", "needle");

        var result = await Grep(("pattern", "needle"), ("output_mode", "content"), ("Glob", "*.cs"));

        Assert.Contains("top.cs", result.Content);
        Assert.Contains("inner.cs", result.Content);
        Assert.DoesNotContain("notes.txt", result.Content);
    }

    [Fact]
    public async Task GrepGlobWithASeparatorFiltersByPathNotJustFileName()
    {
        _temp.WriteFile("src/keep.cs", "needle");
        _temp.WriteFile("other/skip.cs", "needle");

        var result = await Grep(("pattern", "needle"), ("output_mode", "content"), ("Glob", "src/**/*.cs"));

        Assert.Contains("keep.cs", result.Content);
        Assert.DoesNotContain("skip.cs", result.Content);
    }

    [Fact]
    public async Task GrepSpreadsItsBudgetAcrossFilesInsteadOfSpendingItOnTheFirst()
    {
        // 300 hits in the first file would have used the whole 200-match budget.
        _temp.WriteFile("a.txt", string.Join('\n', Enumerable.Repeat("needle", 300)));
        _temp.WriteFile("b.txt", "needle");

        var result = await Grep(("pattern", "needle"), ("output_mode", "content"));

        Assert.Contains("b.txt:1: needle", result.Content);
    }

    [Fact]
    public async Task GrepPrintsContextLinesAroundEachMatch()
    {
        _temp.WriteFile("a.txt", "one\ntwo\nneedle\nfour\nfive");

        var result = await Grep(("pattern", "needle"), ("output_mode", "content"), ("context_lines", 1));

        Assert.Contains("a.txt-2- two", result.Content);
        Assert.Contains("a.txt:3: needle", result.Content);
        Assert.Contains("a.txt-4- four", result.Content);
        Assert.DoesNotContain("one", result.Content);
        Assert.DoesNotContain("five", result.Content);
    }

    [Fact]
    public async Task GrepFilesWithMatchesModeListsPathsWithoutTheLines()
    {
        _temp.WriteFile("a.txt", "needle here");
        _temp.WriteFile("b.txt", "nothing");

        var result = await Grep(("pattern", "needle"), ("output_mode", "files_with_matches"));

        Assert.Equal("a.txt", result.Content.Trim());
    }

    [Fact]
    public async Task GrepCountModeReportsPerFileTotalsAndASum()
    {
        _temp.WriteFile("a.txt", "needle\nneedle");
        _temp.WriteFile("b.txt", "needle");

        var result = await Grep(("pattern", "needle"), ("output_mode", "count"));

        Assert.Contains("a.txt: 2", result.Content);
        Assert.Contains("b.txt: 1", result.Content);
        Assert.Contains("Total: 3 match(es) in 2 file(s).", result.Content);
    }

    [Fact]
    public async Task GrepCountModeReportsTheRealTotalNotTheContentBudget()
    {
        // The per-file cap that keeps content output balanced must not reach counting,
        // or the files worth counting are exactly the ones reported wrong.
        _temp.WriteFile("a.txt", string.Join('\n', Enumerable.Repeat("needle", 45)));

        var result = await Grep(("pattern", "needle"), ("output_mode", "count"));

        Assert.Contains("a.txt: 45", result.Content);
    }

    [Fact]
    public async Task GrepMultilineMatchesAcrossALineBreak()
    {
        _temp.WriteFile("a.txt", "alpha\nbeta");

        var joined = await Grep(("pattern", "alpha.beta"), ("output_mode", "content"), ("multiline", true));
        var perLine = await Grep(("pattern", "alpha.beta"), ("output_mode", "content"));

        Assert.Contains("a.txt:1:", joined.Content);
        Assert.Contains("No matches", perLine.Content);
    }

    [Fact]
    public async Task GrepUnknownOutputModeIsAnError()
    {
        var result = await Grep(("pattern", "x"), ("output_mode", "everything"));

        Assert.True(result.IsError);
        Assert.Contains("output_mode", result.Content);
    }

    [Fact]
    public async Task GrepPathsStayRelativeToTheWorkingDirectoryNotTheSearchRoot()
    {
        _temp.WriteFile("sub/deep.txt", "needle");

        var result = await Grep(("pattern", "needle"), ("output_mode", "content"), ("path", "sub"));

        Assert.Contains(Path.Combine("sub", "deep.txt") + ":1: needle", result.Content);
    }

    [Fact]
    public async Task SearchToolsSkipGitIgnoredFilesUnlessAskedNotTo()
    {
        InitGitRepo();
        _temp.WriteFile(".gitignore", "secret.txt\n");
        _temp.WriteFile("secret.txt", "needle");
        _temp.WriteFile("open.txt", "needle");

        var ignored = await Grep(("pattern", "needle"));
        var everything = await Grep(("pattern", "needle"), ("no_ignore", true));
        var listed = await new GlobTool().ExecuteAsync(Args(("pattern", "**/*.txt")), Context, default);

        Assert.Contains("open.txt", ignored.Content);
        Assert.DoesNotContain("secret.txt", ignored.Content);
        Assert.Contains("secret.txt", everything.Content);
        Assert.DoesNotContain("secret.txt", listed.Content);
    }

    [Fact]
    public async Task NoIgnoreAlsoReachesTheBuildDirectoriesNormallySkipped()
    {
        _temp.WriteFile("bin/generated.txt", "needle");

        var skipped = await Grep(("pattern", "needle"));
        var everything = await Grep(("pattern", "needle"), ("no_ignore", true));

        Assert.Contains("No matches", skipped.Content);
        Assert.Contains("generated.txt", everything.Content);
    }

    private void InitGitRepo()
    {
        Git("init", "-b", "main");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Test");
    }

    private void Git(params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _temp.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);
        using var process = Process.Start(startInfo)!;
        process.WaitForExit(15_000);
        Assert.Equal(0, process.ExitCode);
    }
}
