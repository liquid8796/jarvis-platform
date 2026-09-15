namespace JarvisCode.Parity.Tests;

/// <summary>
/// The tests that launch the real app, run one at a time.
///
/// xunit runs test classes in parallel by default, which for everything else
/// here is free — reading bytes and comparing strings does not care. These
/// three start a window, wait for it to lay out and drive its controls, and
/// three of those at once measure the machine's scheduling rather than the app:
/// the subagent self-test failed exactly once that way, on the 400ms it gives
/// the transcript to render.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AppLaunchCollection
{
    public const string Name = "app launch";
}
