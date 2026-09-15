using JarvisCode.App.Services;
using JarvisCode.Core.Mcp;

namespace JarvisCode.App.Tests.Services;

/// <summary>The Connectors table: which cell each state prints, and what the filter keeps.</summary>
public sealed class ConnectorListPresentationTests
{
    private static readonly IReadOnlyDictionary<string, int> NoTools =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, McpConnectFailure> NoFailures =
        new Dictionary<string, McpConnectFailure>(StringComparer.OrdinalIgnoreCase);

    private static McpServerConfig Stdio(string name) =>
        new(name, "npx", ["-y", "server"], new Dictionary<string, string>());

    private static McpServerConfig Remote(string name, string url = "https://mcp.example.com/mcp") =>
        new(name, "", [], new Dictionary<string, string>()) { Type = "http", Url = url };

    [Fact]
    public void AStdioServerIsDesktopAndARemoteOneIsWeb()
    {
        var local = ConnectorListPresentation.ToRow(Stdio("local"), NoTools, NoFailures, true, null);
        var remote = ConnectorListPresentation.ToRow(Remote("remote"), NoTools, NoFailures, true, null);
        var plugged = ConnectorListPresentation.ToRow(Stdio("plugged"), NoTools, NoFailures, false, "devkit");

        Assert.Equal(ConnectorKind.Desktop, local.Kind);
        Assert.Equal(ConnectorKind.Web, remote.Kind);
        Assert.Equal(ConnectorKind.Plugin, plugged.Kind);
        Assert.Equal("Desktop", ConnectorListPresentation.KindLabel(local.Kind));
        Assert.Equal("Web", ConnectorListPresentation.KindLabel(remote.Kind));
        Assert.Equal("Plugin", ConnectorListPresentation.KindLabel(plugged.Kind));
    }

    [Fact]
    public void AConnectedServerCarriesItsToolCount()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["local"] = 3 };

        var row = ConnectorListPresentation.ToRow(Stdio("local"), counts, NoFailures, true, null);

        Assert.Equal(ConnectorStatus.Connected, row.Status);
        Assert.Equal(3, row.ToolCount);
        Assert.Equal("Connected · 3 tools", ConnectorListPresentation.ToolsLabel(row.ToolCount));
        Assert.Equal("Connected · 1 tool", ConnectorListPresentation.ToolsLabel(1));
        Assert.Equal("This connector has no tools available", ConnectorListPresentation.ToolsLabel(0));
    }

    [Fact]
    public void A401IsToldApartFromEveryOtherFailure()
    {
        var failures = new Dictionary<string, McpConnectFailure>(StringComparer.OrdinalIgnoreCase)
        {
            ["auth"] = new("needs sign-in", NeedsAuthentication: true, DateTimeOffset.Now),
            ["broken"] = new("connection refused", NeedsAuthentication: false, DateTimeOffset.Now),
        };

        var auth = ConnectorListPresentation.ToRow(Remote("auth"), NoTools, failures, true, null);
        var broken = ConnectorListPresentation.ToRow(Stdio("broken"), NoTools, failures, true, null);

        Assert.Equal(ConnectorStatus.NeedsAuthentication, auth.Status);
        Assert.Equal(ConnectorStatus.FailedToConnect, broken.Status);
        Assert.True(auth.NeedsAttention);
        Assert.True(broken.NeedsAttention);
        Assert.Equal("Needs authentication", ConnectorListPresentation.StatusLabel(auth.Status));
        Assert.Equal("Failed to connect", ConnectorListPresentation.StatusLabel(broken.Status));
    }

    [Fact]
    public void TheActionFollowsTheStatus()
    {
        Assert.Equal("Disconnect", ConnectorListPresentation.ActionLabel(ConnectorStatus.Connected));
        Assert.Equal("Connect", ConnectorListPresentation.ActionLabel(ConnectorStatus.NotConnected));
        Assert.Equal("Reconnect", ConnectorListPresentation.ActionLabel(ConnectorStatus.NeedsAuthentication));
        Assert.Equal("Reconnect", ConnectorListPresentation.ActionLabel(ConnectorStatus.FailedToConnect));
    }

    [Fact]
    public void APluginRowSaysWhichPluginProvidesIt()
    {
        var row = ConnectorListPresentation.ToRow(Stdio("x"), NoTools, NoFailures, false, "devkit");

        Assert.Equal("Provided by the devkit plugin", ConnectorListPresentation.ProvidedBy(row));
        Assert.False(row.IsRemovable);
    }

    [Fact]
    public void OnlyAUserLevelConnectorCanBeRemoved()
    {
        Assert.True(ConnectorListPresentation.ToRow(Stdio("a"), NoTools, NoFailures, true, null).IsRemovable);
        Assert.False(ConnectorListPresentation.ToRow(Stdio("b"), NoTools, NoFailures, false, null).IsRemovable);
    }

    [Fact]
    public void TheStatusFilterKeepsWhatItNames()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["up"] = 1 };
        var rows = new[]
        {
            ConnectorListPresentation.ToRow(Stdio("up"), counts, NoFailures, true, null),
            ConnectorListPresentation.ToRow(Stdio("down"), counts, NoFailures, true, null),
        };

        Assert.Equal(2, ConnectorListPresentation.Filter(rows, ConnectorStatusFilter.All).Count);
        Assert.Equal(
            ["up"],
            ConnectorListPresentation.Filter(rows, ConnectorStatusFilter.Connected).Select(r => r.Name));
        Assert.Equal(
            ["down"],
            ConnectorListPresentation.Filter(rows, ConnectorStatusFilter.NotConnected).Select(r => r.Name));
    }

    [Fact]
    public void ARowIsFoundByNameEndpointOrPlugin()
    {
        var rows = new[]
        {
            ConnectorListPresentation.ToRow(Remote("alpha", "https://alpha.example/mcp"), NoTools, NoFailures, true, null),
            ConnectorListPresentation.ToRow(Stdio("beta"), NoTools, NoFailures, false, "devkit"),
        };

        Assert.Equal(["alpha"], ConnectorListPresentation.Search(rows, "alpha.example").Select(r => r.Name));
        Assert.Equal(["beta"], ConnectorListPresentation.Search(rows, "devkit").Select(r => r.Name));
        Assert.Equal(["beta"], ConnectorListPresentation.Search(rows, "npx").Select(r => r.Name));
    }

    [Fact]
    public void AttentionRowsComeOutFirst()
    {
        var failures = new Dictionary<string, McpConnectFailure>(StringComparer.OrdinalIgnoreCase)
        {
            ["zeta"] = new("x", NeedsAuthentication: true, DateTimeOffset.Now),
        };
        var rows = new[]
        {
            ConnectorListPresentation.ToRow(Stdio("alpha"), NoTools, failures, true, null),
            ConnectorListPresentation.ToRow(Remote("zeta"), NoTools, failures, true, null),
        };

        var (attention, main) = ConnectorListPresentation.Split(rows);

        Assert.Equal(["zeta"], attention.Select(r => r.Name));
        Assert.Equal(["alpha"], main.Select(r => r.Name));
    }

    [Fact]
    public void TheEmptyLineNamesWhichControlEmptiedTheList()
    {
        Assert.Equal(
            "No connectors match your search",
            ConnectorListPresentation.EmptyLabel(ConnectorStatusFilter.All, searching: true));
        Assert.Equal(
            "No connected connectors",
            ConnectorListPresentation.EmptyLabel(ConnectorStatusFilter.Connected, searching: false));
        Assert.Equal(
            "No connectors to connect",
            ConnectorListPresentation.EmptyLabel(ConnectorStatusFilter.NotConnected, searching: false));
    }
}

/// <summary>The two-step Add custom connector form.</summary>
public sealed class CustomConnectorFormTests
{
    [Fact]
    public void APathEndingInSseTakesTheLegacyTransport()
    {
        Assert.Equal(CustomConnectorForm.Sse, CustomConnectorForm.TransportFor("https://x.example/sse"));
        Assert.Equal(CustomConnectorForm.Sse, CustomConnectorForm.TransportFor("https://x.example/v1/sse/"));
        Assert.Equal(CustomConnectorForm.StreamableHttp, CustomConnectorForm.TransportFor("https://x.example/mcp"));
        Assert.Equal(CustomConnectorForm.StreamableHttp, CustomConnectorForm.TransportFor(null));
        Assert.Equal(CustomConnectorForm.StreamableHttp, CustomConnectorForm.TransportFor("not a url"));
    }

    [Fact]
    public void OnlyHttpsIsAccepted()
    {
        Assert.Equal("URL must start with ‘https’", CustomConnectorForm.UrlError("http://x.example/mcp"));
        Assert.Equal("Invalid URL format", CustomConnectorForm.UrlError("https:/broken"));
        Assert.Null(CustomConnectorForm.UrlError("https://x.example/mcp"));
        Assert.Null(CustomConnectorForm.UrlError("   "));
    }

    [Fact]
    public void ATakenNameIsRefusedByName()
    {
        Assert.Equal(
            "Connector named ‘Linear’ already exists",
            CustomConnectorForm.NameError("Linear", ["linear", "other"]));
        Assert.Null(CustomConnectorForm.NameError("Fresh", ["linear"]));
        Assert.Null(CustomConnectorForm.NameError("", ["linear"]));
    }

    [Fact]
    public void ContinueNeedsBothFieldsAndNoError()
    {
        Assert.False(CustomConnectorForm.CanContinue("", "https://x.example/mcp", []));
        Assert.False(CustomConnectorForm.CanContinue("name", "", []));
        Assert.False(CustomConnectorForm.CanContinue("name", "http://x.example", []));
        Assert.False(CustomConnectorForm.CanContinue("taken", "https://x.example/mcp", ["taken"]));
        Assert.True(CustomConnectorForm.CanContinue("name", "https://x.example/mcp", []));
    }

    [Fact]
    public void TheFormDescribesARemoteServerWithItsOptionalCredentials()
    {
        var config = CustomConnectorForm.ToConfig(
            " Linear ", " https://mcp.linear.app/sse ", CustomConnectorForm.Sse, " client ", "  ");

        Assert.Equal("Linear", config.Name);
        Assert.Equal("https://mcp.linear.app/sse", config.Url);
        Assert.Equal("sse", config.Type);
        Assert.True(config.IsRemote);
        Assert.Equal("client", config.OAuthClientId);
        Assert.Null(config.OAuthClientSecret);
    }

    [Fact]
    public void OAuthCredentialsSurviveASaveAndReload()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "jarvis-mcp-" + Guid.NewGuid().ToString("N")[..8], "mcp.json");
        try
        {
            McpConfig.SaveUserServers(path,
            [
                CustomConnectorForm.ToConfig("Linear", "https://mcp.linear.app/mcp", null, "id-1", "secret-1"),
            ]);

            var reloaded = Assert.Single(McpConfig.LoadSingleFile(path));

            Assert.Equal("id-1", reloaded.OAuthClientId);
            Assert.Equal("secret-1", reloaded.OAuthClientSecret);
            Assert.Equal("http", reloaded.Type);
        }
        finally
        {
            try
            {
                System.IO.Directory.Delete(System.IO.Path.GetDirectoryName(path)!, recursive: true);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>The sign-in dialog's own rules.</summary>
public sealed class ConnectorSignInTests
{
    [Fact]
    public void OnlyAnHttpsAuthorizeUrlMayBeOpened()
    {
        Assert.True(ConnectorSignIn.IsOpenable("https://auth.example/authorize"));
        Assert.False(ConnectorSignIn.IsOpenable("http://auth.example/authorize"));
        Assert.False(ConnectorSignIn.IsOpenable("not a url"));
    }

    [Fact]
    public void TheSentencesNameTheConnector()
    {
        Assert.Equal(
            "Finish signing in to Linear in your browser, then click Done.",
            ConnectorSignIn.WaitingHeadline("Linear"));
        Assert.Equal(
            "The browser didn’t return for Linear. If you finished signing in, paste the callback URL below.",
            ConnectorSignIn.BrowserDidNotReturn("Linear"));
        Assert.Equal("Connected to Linear.", ConnectorSignIn.Connected("Linear"));
        Assert.Equal("Callback URL for Linear", ConnectorSignIn.CallbackLabel("Linear"));
        Assert.Equal(
            "Refused to open sign-in URL for Linear: must be https",
            ConnectorSignIn.RefusedNonHttps("Linear"));
    }
}
