using System.Text.RegularExpressions;
using JarvisCode.Core.Models;

namespace JarvisCode.App.Services;

/// <summary>
/// The co-author the shell tool docs ask the model to credit on its commits.
/// </summary>
/// <remarks>
/// The reference's Bash doc ends with "Co-Authored-By: Claude {model} &lt;noreply@anthropic.com&gt;",
/// naming the model it runs on — measured on CLI 2.1.257 as "Claude Opus 5",
/// "Claude Fable 5.1", "Claude Opus 4.5", "Claude Sonnet 4.5", "Claude Haiku 4.5".
/// The name is the family and version read off the model id, which is what the
/// reference's catalog display name is for these models; a model outside the
/// Claude families keeps its own display name, since crediting Claude for another
/// vendor's commits would be a false trailer.
/// </remarks>
internal static partial class CommitTrailers
{
    [GeneratedRegex(@"(opus|sonnet|haiku|fable|mythos)-(\d+)(?:-(\d{1,2}))?(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex Family();

    /// <summary>The name after "Co-Authored-By: " for this model.</summary>
    internal static string Name(ModelInfo model) => Name(model.ModelId, model.DisplayName);

    /// <summary>The same, from an id alone, with what to say when the id names no Claude family.</summary>
    internal static string Name(string modelId, string fallback)
    {
        var match = Family().Match(modelId);
        if (!match.Success)
        {
            return fallback;
        }

        var family = char.ToUpperInvariant(match.Groups[1].Value[0]) + match.Groups[1].Value[1..];
        var version = match.Groups[3].Success ? $"{match.Groups[2].Value}.{match.Groups[3].Value}" : match.Groups[2].Value;
        return $"Claude {family} {version}";
    }

    /// <summary>The token the captured shell docs carry where the model name goes.</summary>
    internal const string Token = "{{TRAILER_MODEL}}";
}
