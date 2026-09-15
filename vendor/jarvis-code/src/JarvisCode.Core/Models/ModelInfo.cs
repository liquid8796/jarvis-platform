namespace JarvisCode.Core.Models;

/// <summary>
/// A selectable model entry: which provider serves it, its context budget and
/// list prices per million tokens (0 = unknown, cost shown as unavailable).
/// </summary>
public sealed record ModelInfo(
    string ProviderId,
    string ModelId,
    string DisplayName,
    int MaxContextTokens,
    double InputPricePerMTok = 0,
    double OutputPricePerMTok = 0)
{
    public override string ToString() => DisplayName;
}
