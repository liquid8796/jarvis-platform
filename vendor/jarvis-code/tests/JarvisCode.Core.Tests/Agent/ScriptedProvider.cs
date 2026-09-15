using System.Runtime.CompilerServices;
using JarvisCode.Core.Providers;

namespace JarvisCode.Core.Tests.Agent;

/// <summary>Provider that replays scripted event sequences, one array per model call.</summary>
public sealed class ScriptedProvider(params ProviderEvent[][] turns) : ILlmProvider
{
    private int _call;

    public string Id { get; init; } = "scripted";
    public string DisplayName => "Scripted";
    public List<LlmRequest> Requests { get; } = [];

    public async IAsyncEnumerable<ProviderEvent> StreamChatAsync(
        LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (_call >= turns.Length)
            throw new InvalidOperationException("Provider called more times than scripted.");
        foreach (var providerEvent in turns[_call++])
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return providerEvent;
        }
    }
}
