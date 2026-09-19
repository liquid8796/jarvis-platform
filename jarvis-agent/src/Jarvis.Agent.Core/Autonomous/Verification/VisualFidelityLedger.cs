namespace Jarvis.Agent.Core.Autonomous.Verification;

public enum VisualFidelityCategory
{
    Layout,
    Typography,
    Color,
    Iconography,
    Overflow,
    InteractionState
}

public sealed record VisualFidelityMismatch(
    string Id,
    VisualFidelityCategory Category,
    string Target,
    string Expected,
    string Actual,
    bool Blocking = true,
    bool Resolved = false,
    string? Resolution = null);

public sealed record VisualFidelityResult(
    bool Passed,
    IReadOnlyList<VisualFidelityMismatch> Mismatches,
    IReadOnlyList<VisualFidelityMismatch> UnresolvedBlocking);

/// <summary>
/// Bounded immutable ledger for visual/reference mismatches discovered during rendered QA.
/// </summary>
public sealed record VisualFidelityLedger
{
    private VisualFidelityLedger(IReadOnlyList<VisualFidelityMismatch> mismatches) => Mismatches = mismatches;

    public const int MaxEntries = 128;
    public IReadOnlyList<VisualFidelityMismatch> Mismatches { get; }

    public static VisualFidelityLedger Create(IReadOnlyList<VisualFidelityMismatch> mismatches)
    {
        ArgumentNullException.ThrowIfNull(mismatches);
        if (mismatches.Count > MaxEntries)
            throw new ArgumentOutOfRangeException(nameof(mismatches), $"Visual fidelity ledger supports at most {MaxEntries} entries.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<VisualFidelityMismatch>(mismatches.Count);
        foreach (var mismatch in mismatches)
        {
            if (string.IsNullOrWhiteSpace(mismatch.Id)) throw new ArgumentException("Mismatch id must be non-empty.", nameof(mismatches));
            if (!seen.Add(mismatch.Id)) throw new ArgumentException($"Duplicate visual mismatch id '{mismatch.Id}'.", nameof(mismatches));
            if (string.IsNullOrWhiteSpace(mismatch.Target) || string.IsNullOrWhiteSpace(mismatch.Expected) || string.IsNullOrWhiteSpace(mismatch.Actual))
                throw new ArgumentException("Visual mismatch target, expected and actual values must be non-empty.", nameof(mismatches));
            normalized.Add(mismatch);
        }
        return new(normalized.ToArray());
    }

    public VisualFidelityLedger Resolve(string id, string? resolution = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var found = false;
        var updated = Mismatches.Select(item =>
        {
            if (!string.Equals(item.Id, id, StringComparison.Ordinal)) return item;
            found = true;
            return item with { Resolved = true, Resolution = string.IsNullOrWhiteSpace(resolution) ? item.Resolution : resolution };
        }).ToArray();
        if (!found) throw new KeyNotFoundException($"Visual mismatch '{id}' was not found.");
        return new(updated);
    }

    public VisualFidelityResult Evaluate()
    {
        var unresolved = Mismatches
            .Where(item => item.Blocking && !item.Resolved)
            .OrderBy(item => item.Category)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        // This ledger records discovered issues; an empty list does not prove an image comparison happened.
        return new(Mismatches.Count > 0 && unresolved.Length == 0, Mismatches.ToArray(), unresolved);
    }

    public FrontendEvidence ToEvidence()
    {
        var result = Evaluate();
        var summary = result.Passed
            ? $"Visual fidelity passed: {Mismatches.Count} ledger entr{(Mismatches.Count == 1 ? "y" : "ies")}, no unresolved blocking mismatches."
            : Mismatches.Count == 0 ? "Visual fidelity has no recorded comparison observations. Submit a bound screenshot review."
            : "Visual fidelity blocked by: " + string.Join(", ", result.UnresolvedBlocking.Select(item => item.Id));
        return new FrontendEvidence(FrontendEvidenceKind.VisualFidelity, result.Passed, summary);
    }
}
