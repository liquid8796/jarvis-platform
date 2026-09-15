using Xunit;

namespace JarvisCode.Testing;

internal static class NativeUiTests
{
    public const string Variable = "JARVIS_PARITY_NATIVE_UI";
    public const string DisabledReason = "Native UI tests are opt-in; set JARVIS_PARITY_NATIVE_UI=1 only for an explicitly selected UI check.";
    public static bool Enabled => Environment.GetEnvironmentVariable(Variable) == "1";
}

internal class NativeUiFactAttribute : FactAttribute
{
    public NativeUiFactAttribute()
    {
        if (!NativeUiTests.Enabled) Skip = NativeUiTests.DisabledReason;
    }
}

internal class NativeUiTheoryAttribute : TheoryAttribute
{
    public NativeUiTheoryAttribute()
    {
        if (!NativeUiTests.Enabled) Skip = NativeUiTests.DisabledReason;
    }
}
