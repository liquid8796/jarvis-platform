using System.Text.Json.Serialization;

namespace JarvisCode.App.Theming;

/// <summary>
/// A named color theme: two token maps (light/dark variants) plus an optional
/// loading-spinner replacement. Token keys are CSS-custom-property names
/// ("--bg-000"); HSL values are "H S% L%" triplets, --claude-* values are hex.
/// </summary>
public sealed class ThemeDefinition
{
    public required string Key { get; init; }
    public string? Name { get; init; }
    public string? Category { get; init; }
    public required IReadOnlyDictionary<string, string> Light { get; init; }
    public required IReadOnlyDictionary<string, string> Dark { get; init; }
    public SpinnerSpec? Spinner { get; init; }

    public string DisplayName => Name ?? ToDisplayName(Key);

    public static string ToDisplayName(string key)
    {
        var parts = key.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }
}

public sealed class SpinnerSpec
{
    [JsonPropertyName("viewBox")]
    public string ViewBox { get; init; } = "0 0 100 100";

    /// <summary>spin | bounce | pulse | flip</summary>
    [JsonPropertyName("animation")]
    public string Animation { get; init; } = "spin";

    [JsonPropertyName("paths")]
    public IReadOnlyList<SpinnerPath> Paths { get; init; } = [];

    /// <summary>Second frame for 2-frame "flip" spinners; empty for the rest.</summary>
    [JsonPropertyName("paths2")]
    public IReadOnlyList<SpinnerPath> Paths2 { get; init; } = [];
}

public sealed class SpinnerPath
{
    [JsonPropertyName("d")]
    public string D { get; init; } = "";

    /// <summary>Explicit fill hex; null means "use the accent color".</summary>
    [JsonPropertyName("fill")]
    public string? Fill { get; init; }
}
