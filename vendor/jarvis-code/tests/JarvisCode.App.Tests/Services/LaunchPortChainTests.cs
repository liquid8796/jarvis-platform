using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The reference's port chain for a launch.json entry, measured on desktop
/// 1.46388.2.0: the entry's own <c>port</c>, then the port its <c>url</c> names,
/// then whatever its command spells out, then 3000 for an entry that has a
/// command at all.
/// </summary>
public sealed class LaunchPortChainTests
{
    private static LaunchConfiguration Only(string entry) =>
        Assert.Single(PreviewServers.ParseLaunchFile($$"""
            { "version": "0.0.1", "configurations": [ {{entry}} ] }
            """));

    [Fact]
    public void TheEntrysOwnPortWinsOutright()
    {
        var entry = Only("""
            { "name": "web", "runtimeExecutable": "npm", "runtimeArgs": ["run", "dev", "--port", "9999"],
              "port": 4321, "url": "http://localhost:4321" }
            """);

        Assert.Equal(4321, entry.Port);
    }

    [Fact]
    public void AUrlNamesThePortWhenTheEntryDoesNot()
    {
        var entry = Only("""
            { "name": "attach", "url": "http://localhost:8443" }
            """);

        Assert.Equal(8443, entry.Port);
    }

    /// <summary>A url on the scheme's own port names nothing this chain can use.</summary>
    [Fact]
    public void ADefaultPortUrlIsNotAPort()
    {
        var entry = Only("""
            { "name": "attach", "url": "https://staging.example.com/app" }
            """);

        Assert.Equal(0, entry.Port);
    }

    [Fact]
    public void EnvPortBeatsTheCommandLine()
    {
        var entry = Only("""
            { "name": "web", "runtimeExecutable": "npm", "runtimeArgs": ["run", "dev", "--port", "5173"],
              "env": { "PORT": "7000" } }
            """);

        Assert.Equal(7000, entry.Port);
    }

    [Theory]
    // The flag and its value as two tokens, which is how a launch.json writes it.
    [InlineData("""["run", "dev", "--port", "5173"]""", 5173)]
    [InlineData("""["serve", "-p", "4200"]""", 4200)]
    // The flag carrying its own value.
    [InlineData("""["run", "dev", "--port=5174"]""", 5174)]
    [InlineData("""["serve", "-p=4201"]""", 4201)]
    // One token holding a whole command line, which the second pass reads.
    [InlineData("""["-c", "vite --port 5175"]""", 5175)]
    [InlineData("""["-c", "wait-on http://localhost:5176 && open"]""", 5176)]
    [InlineData("""["-c", "serve :8080"]""", 8080)]
    public void TheCommandLineIsReadForAPort(string runtimeArgs, int expected)
    {
        var entry = Only($$"""
            { "name": "web", "runtimeExecutable": "npm", "runtimeArgs": {{runtimeArgs}} }
            """);

        Assert.Equal(expected, entry.Port);
    }

    /// <summary>
    /// The reference searches program and args too, not only the runtime pair -
    /// its token list is [program, runtimeExecutable, ...runtimeArgs, ...args].
    /// </summary>
    [Fact]
    public void ProgramAndArgsAreSearchedAsWell()
    {
        var entry = Only("""
            { "name": "api", "program": "./server.js", "args": ["--port", "6001"] }
            """);

        Assert.Equal(6001, entry.Port);
        Assert.Equal("./server.js", entry.Program);
        Assert.Equal(["--port", "6001"], entry.Args);
    }

    /// <summary>
    /// Each pattern is tried across every token before the next pattern is tried
    /// at all, so a later token carrying an explicit flag beats an earlier one
    /// that merely looks like a host and port.
    /// </summary>
    [Fact]
    public void AFlagAnywhereBeatsABareHostAndPortEarlier()
    {
        var entry = Only("""
            { "name": "web", "runtimeExecutable": "sh",
              "runtimeArgs": ["-c", "proxy localhost:3100", "--port 5180"] }
            """);

        Assert.Equal(5180, entry.Port);
    }

    [Fact]
    public void AnEntryWithACommandAndNoPortFallsBackTo3000()
    {
        var entry = Only("""
            { "name": "web", "runtimeExecutable": "npm", "runtimeArgs": ["run", "dev"] }
            """);

        Assert.Equal(3000, entry.Port);
    }

    /// <summary>
    /// An entry with neither a command nor anything naming a port keeps 0, which
    /// is what "attach to whatever the url says" means.
    /// </summary>
    [Fact]
    public void AnAttachEntryWithNothingToGoOnKeepsNoPort()
    {
        var entry = Only("""
            { "name": "attach" }
            """);

        Assert.Equal(0, entry.Port);
        Assert.True(entry.AttachOnly);
    }
}
