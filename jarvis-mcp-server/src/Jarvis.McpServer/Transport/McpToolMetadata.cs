namespace Jarvis.McpServer.Transport;

internal static class McpToolMetadata
{
    public static string TitleFor(string name)
    {
        var normalized = name.Replace("__", "_", StringComparison.Ordinal).Replace('.', '_');
        return string.Join(' ', normalized.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(SplitIdentifierWord));
    }

    private static string SplitIdentifierWord(string word)
    {
        var splitAcronym = System.Text.RegularExpressions.Regex.Replace(word, "([A-Z]+)([A-Z][a-z])", "$1 $2");
        var splitCase = System.Text.RegularExpressions.Regex.Replace(splitAcronym, "([a-z0-9])([A-Z])", "$1 $2");
        return splitCase.Length == 0 ? splitCase : char.ToUpperInvariant(splitCase[0]) + splitCase[1..];
    }
}
