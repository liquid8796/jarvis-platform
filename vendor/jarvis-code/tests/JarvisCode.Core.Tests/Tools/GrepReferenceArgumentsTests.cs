using System.Text.Json.Nodes;
using JarvisCode.Core.Tools;
using JarvisCode.Core.Tools.BuiltIn;

namespace JarvisCode.Core.Tests.Tools;

/// <summary>
/// The reference Grep tool's ripgrep-shaped arguments (CLI 2.1.257 schema), on
/// this tool: glob, type, -i, -n, -o, -A/-B/-C, head_limit and offset.
/// </summary>
public sealed class GrepReferenceArgumentsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private ToolExecutionContext Context => new() { WorkingDirectory = _temp.Path };

    public GrepReferenceArgumentsTests()
    {
        File.WriteAllText(Path.Combine(_temp.Path, "a.cs"), "one\nTwo alpha\nthree\nfour alpha beta\nfive\nsix\n");
        File.WriteAllText(Path.Combine(_temp.Path, "b.txt"), "alpha in text\nnothing\n");
        File.WriteAllText(Path.Combine(_temp.Path, "c.py"), "ALPHA upper\n");
    }

    public void Dispose() => _temp.Dispose();

    private static JsonObject Args(params (string Key, JsonNode? Value)[] pairs)
    {
        var args = new JsonObject();
        foreach (var (key, value) in pairs)
        {
            args[key] = value;
        }

        return args;
    }

    private async Task<string> Grep(params (string Key, JsonNode? Value)[] pairs)
    {
        var result = await new GrepTool().ExecuteAsync(Args(pairs), Context, default);
        Assert.False(result.IsError, result.Content);
        return result.Content;
    }

    [Fact]
    public async Task Glob_filters_files_and_the_old_capitalised_name_still_works()
    {
        var lower = await Grep(("pattern", "alpha"), ("glob", "*.txt"));
        Assert.Contains("b.txt", lower);
        Assert.DoesNotContain("a.cs", lower);

        var legacy = await Grep(("pattern", "alpha"), ("Glob", "*.txt"));
        Assert.Equal(lower, legacy);
    }

    [Fact]
    public async Task Type_selects_a_file_type_and_an_unknown_type_is_refused()
    {
        var cs = await Grep(("pattern", "alpha"), ("type", "cs"));
        Assert.Contains("a.cs", cs);
        Assert.DoesNotContain("b.txt", cs);

        var result = await new GrepTool().ExecuteAsync(Args(("pattern", "x"), ("type", "cobol9")), Context, default);
        Assert.True(result.IsError);
        Assert.Contains("Unknown file type 'cobol9'", result.Content);
    }

    [Fact]
    public async Task Dash_i_matches_case_insensitively()
    {
        var sensitive = await Grep(("pattern", "ALPHA"), ("output_mode", "files_with_matches"));
        Assert.Contains("c.py", sensitive);
        Assert.DoesNotContain("a.cs", sensitive);

        var insensitive = await Grep(("pattern", "ALPHA"), ("-i", true), ("output_mode", "files_with_matches"));
        Assert.Contains("a.cs", insensitive);
        Assert.Contains("c.py", insensitive);
    }

    [Fact]
    public async Task Dash_A_and_dash_B_set_each_side_of_the_context_on_their_own()
    {
        var after = await Grep(("pattern", "^three$"), ("output_mode", "content"), ("-A", 2), ("glob", "a.cs"));
        Assert.Contains("a.cs:3: three", after);
        Assert.Contains("a.cs-4- four alpha beta", after);
        Assert.Contains("a.cs-5- five", after);
        Assert.DoesNotContain("a.cs-2-", after);

        var before = await Grep(("pattern", "^three$"), ("output_mode", "content"), ("-B", 1), ("glob", "a.cs"));
        Assert.Contains("a.cs-2- Two alpha", before);
        Assert.DoesNotContain("a.cs-4-", before);

        var both = await Grep(("pattern", "^three$"), ("output_mode", "content"), ("-C", 1), ("glob", "a.cs"));
        Assert.Contains("a.cs-2- Two alpha", both);
        Assert.Contains("a.cs-4- four alpha beta", both);
    }

    [Fact]
    public async Task Dash_n_false_drops_the_line_numbers()
    {
        var numbered = await Grep(("pattern", "three"), ("output_mode", "content"), ("glob", "a.cs"));
        Assert.Contains("a.cs:3: three", numbered);

        var plain = await Grep(("pattern", "three"), ("output_mode", "content"), ("glob", "a.cs"), ("-n", false));
        Assert.Contains("a.cs: three", plain);
        Assert.DoesNotContain("a.cs:3:", plain);
    }

    [Fact]
    public async Task Dash_o_prints_only_the_matched_parts()
    {
        var only = await Grep(("pattern", "alpha|beta"), ("output_mode", "content"), ("glob", "a.cs"), ("-o", true));
        Assert.Contains("a.cs:4: alpha", only);
        Assert.Contains("a.cs:4: beta", only);
        Assert.DoesNotContain("four alpha beta", only);
    }

    [Fact]
    public void Head_limit_and_offset_page_the_output_the_way_head_and_tail_do()
    {
        var output = string.Join('\n', Enumerable.Range(1, 10).Select(static i => $"line {i}"));

        Assert.Equal(output, GrepTool.Page(output, 0, 0));
        Assert.Equal(output, GrepTool.Page(output, 0, 250));

        var firstThree = GrepTool.Page(output, 0, 3);
        Assert.StartsWith("line 1\nline 2\nline 3\n", firstThree);
        Assert.Contains("7 more line(s); continue with offset=3", firstThree);

        var page = GrepTool.Page(output, 3, 3);
        Assert.StartsWith("line 4\nline 5\nline 6\n", page);
        Assert.Contains("continue with offset=6", page);

        var tail = GrepTool.Page(output, 8, 0);
        Assert.Equal("line 9\nline 10", tail);

        Assert.Contains("past the end", GrepTool.Page(output, 20, 5));
    }

    [Fact]
    public async Task Head_limit_applies_to_the_tool_output()
    {
        var full = await Grep(("pattern", "alpha"), ("output_mode", "files_with_matches"));
        var limited = await Grep(("pattern", "alpha"), ("output_mode", "files_with_matches"), ("head_limit", 1));
        var lines = limited.Split('\n');
        Assert.Equal(full.Split('\n')[0], lines[0]);
        Assert.Equal(2, lines.Length);
        Assert.Contains("more line(s)", lines[1]);
    }

    [Fact]
    public async Task OmittedOutputModeAnswersTheFileListTheSchemaPromises()
    {
        // The reference's own schema says: Defaults to "files_with_matches".
        var omitted = await Grep(("pattern", "alpha"));
        var explicitFiles = await Grep(("pattern", "alpha"), ("output_mode", "files_with_matches"));

        Assert.Equal(explicitFiles, omitted);
        Assert.DoesNotContain(":", omitted.Replace(_temp.Path, string.Empty), StringComparison.Ordinal);
    }
}
