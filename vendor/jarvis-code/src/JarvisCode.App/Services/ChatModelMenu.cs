namespace JarvisCode.App.Services;

/// <summary>
/// The Chat surface's model menu, which is the reference's shared model selector
/// (<c>shared-10-3-tqq7pk.js</c>, its <c>iP</c>; the strings are the design
/// system's own table in <c>index-DEczO-db.js</c>): a trigger reading
/// "Model: {name}", a search box over the list and a "No matches" row when it finds
/// nothing.
///
/// Two of the reference's rows are deliberately not built, and say why in the
/// parity manifest: its "More models" group divides the models the product knows
/// from the ids it does not, and its "Default model" row picks the account's — this
/// build ships no model catalogue at all, so every model here is one the user
/// added and neither division exists.
/// </summary>
public static class ChatModelMenu
{
    public const string SearchModels = "Search models";        // 4mzlmbbRYe
    public const string NoMatches = "No matches";              // 96GJ5wSQam
    public const string LoadingModels = "Loading models…";     // D90Mq4jCm6

    /// <summary>The chip's own label, which the reference reads out as the trigger.</summary>
    public static string Trigger(string name) => $"Model: {name}";   // 0ZMx/tNv6m

    /// <summary>
    /// How many models a list needs before the search box is worth showing. The
    /// reference's selector has no such threshold — its list is the account's
    /// catalogue and always long — so this is this build's own.
    /// </summary>
    public const int SearchAbove = 8;

    /// <summary>Whether a row survives the search box's query.</summary>
    public static bool Matches(string? label, string? query)
    {
        var trimmed = query?.Trim();
        return string.IsNullOrEmpty(trimmed)
            || (label is not null && label.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
    }
}
