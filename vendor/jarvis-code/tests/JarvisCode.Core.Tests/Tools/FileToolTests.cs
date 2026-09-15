using System.Text.Json.Nodes;
using JarvisCode.Core.Tools;
using JarvisCode.Core.Tools.BuiltIn;

namespace JarvisCode.Core.Tests.Tools;

public sealed class FileToolTests : IDisposable
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

    [Fact]
    public async Task ReadFile_ReturnsNumberedLines()
    {
        _temp.WriteFile("a.txt", "first\nsecond");
        var result = await new ReadFileTool().ExecuteAsync(Args(("file_path", "a.txt")), Context, default);

        Assert.False(result.IsError);
        Assert.Contains("1\tfirst", result.Content);
        Assert.Contains("2\tsecond", result.Content);
    }

    [Fact]
    public async Task ReadFile_MissingFile_IsError()
    {
        var result = await new ReadFileTool().ExecuteAsync(Args(("file_path", "missing.txt")), Context, default);
        Assert.True(result.IsError);
        Assert.Contains("not found", result.Content);
    }

    [Fact]
    public async Task ReadFile_OffsetAndLimit_SliceAndContinuationHint()
    {
        _temp.WriteFile("a.txt", string.Join('\n', Enumerable.Range(1, 10).Select(n => $"line{n}")));
        var result = await new ReadFileTool().ExecuteAsync(
            Args(("file_path", "a.txt"), ("offset", 3), ("limit", 2)), Context, default);

        Assert.False(result.IsError);
        Assert.Contains("line3", result.Content);
        Assert.Contains("line4", result.Content);
        Assert.DoesNotContain("line5", result.Content.Split("...")[0]);
        Assert.Contains("offset=5", result.Content);
    }

    [Fact]
    public async Task ReadFile_OversizedFile_IsRefusedWithGuidance()
    {
        var path = Path.Combine(_temp.Path, "huge.txt");
        using (var stream = File.Create(path))
            stream.SetLength(FileSystemDefaults.MaxFileBytes + 1);

        var result = await new ReadFileTool().ExecuteAsync(Args(("file_path", "huge.txt")), Context, default);

        Assert.True(result.IsError);
        Assert.Contains("larger than the 10 MB limit", result.Content);
    }

    [Fact]
    public async Task EditFile_OversizedFile_IsRefused()
    {
        var path = Path.Combine(_temp.Path, "huge.txt");
        using (var stream = File.Create(path))
            stream.SetLength(FileSystemDefaults.MaxFileBytes + 1);

        var result = await new EditFileTool().ExecuteAsync(
            Args(("file_path", "huge.txt"), ("old_string", "a"), ("new_string", "b")), Context, default);

        Assert.True(result.IsError);
        Assert.Contains("10 MB", result.Content);
    }

    [Fact]
    public async Task WriteFile_OversizedContent_IsRefused()
    {
        var content = new string('x', (int)FileSystemDefaults.MaxFileBytes + 1);

        var result = await new WriteFileTool().ExecuteAsync(
            Args(("file_path", "big.txt"), ("content", content)), Context, default);

        Assert.True(result.IsError);
        Assert.Contains("write limit", result.Content);
        Assert.False(File.Exists(Path.Combine(_temp.Path, "big.txt")));
    }

    [Fact]
    public async Task ReadFile_EmptyFile_ReportsEmpty()
    {
        _temp.WriteFile("empty.txt", "");
        var result = await new ReadFileTool().ExecuteAsync(Args(("file_path", "empty.txt")), Context, default);
        Assert.False(result.IsError);
        Assert.Contains("empty", result.Content);
    }

    [Fact]
    public async Task WriteFile_CreatesParentDirectories()
    {
        var result = await new WriteFileTool().ExecuteAsync(
            Args(("file_path", @"nested\dir\out.txt"), ("content", "hello")), Context, default);

        Assert.False(result.IsError);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_temp.Path, "nested", "dir", "out.txt")));
    }

    [Fact]
    public async Task EditFile_ReplacesUniqueString()
    {
        var path = _temp.WriteFile("code.cs", "var x = 1;\nvar y = 2;");
        var result = await new EditFileTool().ExecuteAsync(
            Args(("file_path", path), ("old_string", "var y = 2;"), ("new_string", "var y = 3;")), Context, default);

        Assert.False(result.IsError);
        Assert.Contains("var y = 3;", File.ReadAllText(path));
    }

    [Fact]
    public async Task EditFile_AmbiguousString_IsErrorWithoutReplaceAll()
    {
        var path = _temp.WriteFile("code.cs", "a\na\n");
        var result = await new EditFileTool().ExecuteAsync(
            Args(("file_path", path), ("old_string", "a"), ("new_string", "b")), Context, default);

        Assert.True(result.IsError);
        Assert.Contains("replace_all", result.Content);
        Assert.Equal("a\na\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task EditFile_ReplaceAll_ReplacesEveryOccurrence()
    {
        var path = _temp.WriteFile("code.cs", "a a a");
        var result = await new EditFileTool().ExecuteAsync(
            Args(("file_path", path), ("old_string", "a"), ("new_string", "b"), ("replace_all", true)), Context, default);

        Assert.False(result.IsError);
        Assert.Equal("b b b", File.ReadAllText(path));
    }

    [Fact]
    public async Task EditFile_NotFoundString_IsError()
    {
        var path = _temp.WriteFile("code.cs", "content");
        var result = await new EditFileTool().ExecuteAsync(
            Args(("file_path", path), ("old_string", "missing"), ("new_string", "x")), Context, default);
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Glob_FindsMatchesAndSkipsExcludedDirectories()
    {
        _temp.WriteFile(@"src\a.cs", "x");
        _temp.WriteFile(@"src\deep\b.cs", "x");
        _temp.WriteFile(@"bin\generated.cs", "x");
        var result = await new GlobTool().ExecuteAsync(Args(("pattern", "**/*.cs")), Context, default);

        Assert.False(result.IsError);
        Assert.Contains("a.cs", result.Content);
        Assert.Contains("b.cs", result.Content);
        Assert.DoesNotContain("generated.cs", result.Content);
    }

    [Fact]
    public async Task Glob_NoMatches_SaysSo()
    {
        var result = await new GlobTool().ExecuteAsync(Args(("pattern", "**/*.zig")), Context, default);
        Assert.False(result.IsError);
        Assert.Contains("No files match", result.Content);
    }

    [Fact]
    public async Task Grep_FindsLinesWithPathAndLineNumber()
    {
        _temp.WriteFile("a.txt", "alpha\nbeta\ngamma beta");
        var result = await new GrepTool().ExecuteAsync(Args(("pattern", "beta"), ("output_mode", "content")), Context, default);

        Assert.False(result.IsError);
        Assert.Contains("a.txt:2: beta", result.Content);
        Assert.Contains("a.txt:3: gamma beta", result.Content);
    }

    [Fact]
    public async Task Grep_InvalidRegex_IsError()
    {
        var result = await new GrepTool().ExecuteAsync(Args(("pattern", "([unclosed")), Context, default);
        Assert.True(result.IsError);
        Assert.Contains("Invalid regular expression", result.Content);
    }

    [Fact]
    public async Task ListDirectory_ShowsEntriesAndMarksDirectories()
    {
        _temp.WriteFile("file.txt", "x");
        Directory.CreateDirectory(Path.Combine(_temp.Path, "sub"));
        var result = await new ListDirectoryTool().ExecuteAsync(Args(), Context, default);

        Assert.False(result.IsError);
        Assert.Contains("file.txt", result.Content);
        Assert.Contains("sub" + Path.DirectorySeparatorChar, result.Content);
    }

    [Fact]
    public void Truncate_CapsLongOutput()
    {
        var context = new ToolExecutionContext { WorkingDirectory = _temp.Path, MaxOutputChars = 10 };
        var truncated = context.Truncate(new string('x', 50));
        Assert.Contains("truncated", truncated);
        Assert.StartsWith("xxxxxxxxxx\n", truncated);
    }
}
