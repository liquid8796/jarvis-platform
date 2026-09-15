using JarvisCode.Core.Utilities;

namespace JarvisCode.Core.Tests.Utilities;

/// <summary>
/// The sink is one process-wide slot, so these assertions are only true while
/// nothing else is writing to it: they run in a collection of their own, which
/// xunit schedules apart from the parallel ones.
/// </summary>
[CollectionDefinition(DiagnosticLogCollection.Name, DisableParallelization = true)]
public sealed class DiagnosticLogCollection
{
    public const string Name = "diagnostic-log";
}

[Collection(DiagnosticLogCollection.Name)]
public class DiagnosticLogTests : IDisposable
{
    public void Dispose() => DiagnosticLog.Attach(null);

    [Fact]
    public void WritingWithNoSinkAttachedDoesNothing()
    {
        DiagnosticLog.Attach(null);

        Assert.False(DiagnosticLog.IsEnabled);
        // The engine calls this on every stream, so it must be safe when off.
        DiagnosticLog.Write("ignored");
    }

    [Fact]
    public void AnAttachedSinkReceivesEveryLine()
    {
        var lines = new List<string>();
        DiagnosticLog.Attach(lines.Add);

        Assert.True(DiagnosticLog.IsEnabled);
        DiagnosticLog.Write("first");
        DiagnosticLog.Write("second");

        Assert.Equal(["first", "second"], lines);
    }

    [Fact]
    public void DetachingStopsDelivery()
    {
        var lines = new List<string>();
        DiagnosticLog.Attach(lines.Add);
        DiagnosticLog.Write("kept");
        DiagnosticLog.Attach(null);
        DiagnosticLog.Write("dropped");

        Assert.Equal(["kept"], lines);
        Assert.False(DiagnosticLog.IsEnabled);
    }

    [Fact]
    public void AttachingAgainReplacesTheSink()
    {
        var first = new List<string>();
        var second = new List<string>();
        DiagnosticLog.Attach(first.Add);
        DiagnosticLog.Attach(second.Add);
        DiagnosticLog.Write("line");

        Assert.Empty(first);
        Assert.Equal(["line"], second);
    }
}
