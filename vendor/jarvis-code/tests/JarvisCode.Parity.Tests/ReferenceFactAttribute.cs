namespace JarvisCode.Parity.Tests;

/// <summary>
/// A fact that needs the reference CLI installed. xunit 2.9 has no runtime
/// <c>Assert.Skip</c>, so the condition is evaluated when the attribute is
/// constructed and turns into xunit's own static skip — which is why the reason
/// names what is missing instead of the test silently passing.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class ReferenceCliFactAttribute : FactAttribute
{
    public ReferenceCliFactAttribute()
    {
        if (ReferenceInstall.CliPath is null)
        {
            Skip = ReferenceInstall.CliMissingReason;
        }
    }
}

/// <summary>A theory that needs the reference CLI installed.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class ReferenceCliTheoryAttribute : TheoryAttribute
{
    public ReferenceCliTheoryAttribute()
    {
        if (ReferenceInstall.CliPath is null)
        {
            Skip = ReferenceInstall.CliMissingReason;
        }
    }
}

/// <summary>A fact that needs the packaged desktop app's string catalogue.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class ReferenceAppFactAttribute : FactAttribute
{
    public ReferenceAppFactAttribute()
    {
        if (ReferenceInstall.Catalogue is null)
        {
            Skip = ReferenceInstall.CatalogueMissingReason;
        }
    }
}

/// <summary>A theory that needs the packaged desktop app's string catalogue.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class ReferenceAppTheoryAttribute : TheoryAttribute
{
    public ReferenceAppTheoryAttribute()
    {
        if (ReferenceInstall.Catalogue is null)
        {
            Skip = ReferenceInstall.CatalogueMissingReason;
        }
    }
}

/// <summary>
/// A fact that rewrites one of this suite's checked-in data files from the
/// installed reference, and is skipped unless its environment variable is set.
///
/// The regeneration lives in the suite rather than in a script beside it so the
/// file is produced by the same extraction the assertions read it with; a
/// generator that drifts from its checker is worse than no generator at all.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class ApprovalFactAttribute : FactAttribute
{
    public ApprovalFactAttribute(string variable)
    {
        if (Environment.GetEnvironmentVariable(variable) != "1")
        {
            Skip = $"set {variable}=1 to rewrite this file from the installed reference";
        }
        else if (ReferenceInstall.CliPath is null)
        {
            Skip = ReferenceInstall.CliMissingReason;
        }
    }
}
