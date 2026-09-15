using JarvisCode.Core.Permissions;

namespace JarvisCode.Core.Tests.Permissions;

/// <summary>
/// The reference's <c>permissions.defaultMode</c> in a project's settings files,
/// and the two values it refuses to take from a checked-in file.
/// </summary>
public sealed class ProjectDefaultModeTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private void WriteSettings(string fileName, string json)
    {
        var dir = Path.Combine(_temp.Path, ".jarvis");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), json);
    }

    [Fact]
    public void No_settings_directory_means_no_default()
    {
        Assert.Null(ProjectPermissions.LoadDefaultMode(_temp.Path));
    }

    [Theory]
    [InlineData("acceptEdits", PermissionMode.AcceptEdits)]
    [InlineData("plan", PermissionMode.Plan)]
    [InlineData("default", PermissionMode.Manual)]
    [InlineData("dontAsk", PermissionMode.Auto)]
    public void A_project_file_may_name_an_asking_mode(string spelling, PermissionMode expected)
    {
        WriteSettings("settings.json", $$$"""{"permissions":{"defaultMode":"{{{spelling}}}"}}""");
        Assert.Equal(expected, ProjectPermissions.LoadDefaultMode(_temp.Path));
    }

    /// <summary>
    /// CLI 2.1.257 changed <c>defaultMode: "bypassPermissions"</c> in a project's
    /// settings to be ignored, like "auto": a mode that stops asking comes from
    /// user or managed settings, or the flag — never from the repository.
    /// </summary>
    [Theory]
    [InlineData("bypassPermissions")]
    [InlineData("auto")]
    [InlineData("nonsense")]
    public void Auto_and_bypass_are_ignored_from_a_project_file(string spelling)
    {
        WriteSettings("settings.json", $$$"""{"permissions":{"defaultMode":"{{{spelling}}}"}}""");
        Assert.Null(ProjectPermissions.LoadDefaultMode(_temp.Path));
    }

    [Fact]
    public void The_local_file_wins_over_the_shared_one()
    {
        WriteSettings("settings.json", """{"permissions":{"defaultMode":"acceptEdits"}}""");
        WriteSettings("settings.local.json", """{"permissions":{"defaultMode":"plan"}}""");
        Assert.Equal(PermissionMode.Plan, ProjectPermissions.LoadDefaultMode(_temp.Path));
    }

    [Fact]
    public void A_local_file_that_names_bypass_does_not_hide_the_shared_files_mode()
    {
        WriteSettings("settings.json", """{"permissions":{"defaultMode":"acceptEdits"}}""");
        WriteSettings("settings.local.json", """{"permissions":{"defaultMode":"bypassPermissions"}}""");
        Assert.Equal(PermissionMode.AcceptEdits, ProjectPermissions.LoadDefaultMode(_temp.Path));
    }

    [Fact]
    public void A_malformed_file_is_no_default_rather_than_an_exception()
    {
        WriteSettings("settings.json", "{not json");
        Assert.Null(ProjectPermissions.LoadDefaultMode(_temp.Path));
    }
}
