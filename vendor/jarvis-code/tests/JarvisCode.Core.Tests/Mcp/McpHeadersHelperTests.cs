using JarvisCode.Core.Mcp;

namespace JarvisCode.Core.Tests.Mcp;

/// <summary>
/// The headers helper's parse half — the three ways its output can be wrong —
/// and the reference's four sentences for them.
/// </summary>
public sealed class McpHeadersHelperTests
{
    [Fact]
    public void A_JSON_object_of_strings_is_the_headers()
    {
        var result = McpHeadersHelper.Parse("""{"Authorization": "Bearer abc", "X-Trace": "1"}""");

        Assert.True(result.Ok);
        Assert.Equal("Bearer abc", result.Headers!["Authorization"]);
        Assert.Equal("1", result.Headers["X-Trace"]);
    }

    [Fact]
    public void Header_names_are_matched_the_way_HTTP_matches_them()
    {
        var result = McpHeadersHelper.Parse("""{"authorization": "Bearer abc"}""");

        Assert.True(result.Headers!.ContainsKey("Authorization"));
    }

    [Fact]
    public void Output_that_is_not_JSON_is_parse_failed()
    {
        Assert.Equal(McpHeadersHelperFailure.ParseFailed, McpHeadersHelper.Parse("not json").Failure);
    }

    [Fact]
    public void JSON_that_is_not_an_object_is_non_object()
    {
        Assert.Equal(McpHeadersHelperFailure.NonObject, McpHeadersHelper.Parse("[1, 2]").Failure);
        Assert.Equal(McpHeadersHelperFailure.NonObject, McpHeadersHelper.Parse("\"token\"").Failure);
    }

    [Fact]
    public void A_non_string_value_is_non_string_value()
    {
        Assert.Equal(
            McpHeadersHelperFailure.NonStringValue,
            McpHeadersHelper.Parse("""{"X-Count": 3}""").Failure);
    }

    [Theory]
    [InlineData(McpHeadersHelperFailure.ExecFailed,
        "headersHelper for MCP server 'gh' did not return a valid value")]
    [InlineData(McpHeadersHelperFailure.ParseFailed,
        "headersHelper for MCP server 'gh' did not return valid JSON")]
    [InlineData(McpHeadersHelperFailure.NonObject,
        "headersHelper for MCP server 'gh' must return a JSON object with string key-value pairs")]
    [InlineData(McpHeadersHelperFailure.NonStringValue,
        "headersHelper for MCP server 'gh' returned a non-string header value")]
    public void The_four_failures_carry_the_reference_sentence(McpHeadersHelperFailure failure, string expected)
    {
        Assert.Equal(expected, McpHeadersHelper.Message("gh", failure));
    }

    [Fact]
    public async Task A_server_with_no_helper_resolves_to_no_headers_rather_than_a_failure()
    {
        var config = new McpServerConfig("plain", "", [], new Dictionary<string, string>())
        {
            Type = "http",
            Url = "https://mcp.example",
        };

        var result = await McpHeadersHelper.RunAsync(
            config, Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Empty(result.Headers!);
    }

    [Fact]
    public void The_two_keys_round_trip_through_the_config_file()
    {
        var temp = new TempDirectory();
        try
        {
            var path = Path.Combine(temp.Path, "mcp.json");
            File.WriteAllText(path, """
                {"mcpServers": {"gh": {
                    "type": "http",
                    "url": "https://mcp.example",
                    "headersHelper": "gh auth token | jq -R '{Authorization: (\"Bearer \" + .)}'",
                    "discoveryCache": false
                }}}
                """);

            var server = Assert.Single(McpConfig.LoadSingleFile(path));

            Assert.Equal("gh auth token | jq -R '{Authorization: (\"Bearer \" + .)}'", server.HeadersHelper);
            Assert.False(server.DiscoveryCache);
            // Both belong to the identity a refresh compares, so changing either
            // restarts the server rather than leaving it on the old credential.
            Assert.NotEqual(server.Signature, (server with { HeadersHelper = null }).Signature);
            Assert.NotEqual(server.Signature, (server with { DiscoveryCache = null }).Signature);
        }
        finally
        {
            temp.Dispose();
        }
    }
}
