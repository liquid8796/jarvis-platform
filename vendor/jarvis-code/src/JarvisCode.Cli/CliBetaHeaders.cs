using JarvisCode.Core.Providers;

namespace JarvisCode.Cli;

internal sealed class CliBetaRegistry(IProviderRegistry inner, IReadOnlyList<string> betas) : IProviderRegistry
{
    public IReadOnlyList<ILlmProvider> All => [.. inner.All.Select(provider => new CliBetaProvider(provider, betas))];
    public ILlmProvider Get(string providerId) => new CliBetaProvider(inner.Get(providerId), betas);

    internal static IReadOnlyList<KeyValuePair<string, string>> Merge(
        IReadOnlyList<KeyValuePair<string, string>> headers, IReadOnlyList<string> betas)
    {
        if (betas.Any(beta => string.IsNullOrWhiteSpace(beta) || beta.Any(character => char.IsControl(character) || character == ',')))
            throw new CliError("Beta names must be non-empty and must not contain control characters or commas.");
        if (betas.Count == 0) return headers;
        var all = headers.Where(header => header.Key.Equals("anthropic-beta", StringComparison.OrdinalIgnoreCase))
            .SelectMany(header => header.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Concat(betas).Distinct(StringComparer.Ordinal);
        return [.. headers.Where(header => !header.Key.Equals("anthropic-beta", StringComparison.OrdinalIgnoreCase)),
            new("anthropic-beta", string.Join(',', all))];
    }

    private sealed class CliBetaProvider(ILlmProvider inner, IReadOnlyList<string> betas) : ILlmProvider, IDecoratedProvider
    {
        public ILlmProvider InnerProvider => inner;
        public string Id => inner.Id;
        public string DisplayName => inner.DisplayName;
        public bool RequiresApiKey => inner.RequiresApiKey;
        public IAsyncEnumerable<ProviderEvent> StreamChatAsync(LlmRequest request, CancellationToken cancellationToken) =>
            inner.StreamChatAsync(request with { ExtraHeaders = Merge(request.ExtraHeaders, betas) }, cancellationToken);
    }
}
