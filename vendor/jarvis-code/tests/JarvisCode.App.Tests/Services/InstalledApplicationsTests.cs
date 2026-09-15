using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The gate request_access puts every requested name through. The reference
/// resolves against the installed index and the running applications (its
/// <c>Yt</c>, desktop 1.44121.2.0), and its refusal says so; resolving against
/// the index alone refused an open application in the words of a message that
/// had just promised it would not.
/// </summary>
public class InstalledApplicationsTests
{
    private static readonly string[] Installed = ["Notepad++", "Visual Studio Code"];

    [Fact]
    public void An_indexed_name_resolves_to_the_indexed_spelling()
        => Assert.Equal("Notepad++", InstalledApplications.Resolve("notepad++", Installed, []));

    [Fact]
    public void A_running_application_resolves_even_when_nothing_indexed_it()
        => Assert.Equal("notepad", InstalledApplications.Resolve("Notepad", Installed, ["notepad"]));

    [Fact]
    public void A_name_that_is_neither_installed_nor_running_resolves_to_nothing()
        => Assert.Null(InstalledApplications.Resolve("Notepad", Installed, ["explorer"]));

    [Fact]
    public void An_index_that_has_not_answered_yet_refuses_nothing()
        => Assert.Equal("Anything", InstalledApplications.Resolve("Anything", [], []));

    [Fact]
    public void The_running_list_names_this_test_hosts_own_process()
    {
        // The test host has no window of its own, so the list cannot be asserted
        // to hold it — what is asserted is that the enumeration answers without
        // throwing and names nothing blank.
        var running = InstalledApplications.Running();
        Assert.All(running, name => Assert.False(string.IsNullOrWhiteSpace(name)));
    }

    [Fact]
    public void The_start_menu_index_carries_the_packaged_applications_too()
    {
        // The AppsFolder is what the Start menu lists; the two Programs folders
        // hold shortcut files only, and a packaged application has none. Read on
        // an STA thread, as the scan itself is.
        List<string> names = [];
        var thread = new Thread(() =>
            names = [.. InstalledApplications.AppsFolderNames(CancellationToken.None)]);
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "the AppsFolder enumeration did not answer");

        Assert.NotEmpty(names);
        Assert.All(names, name => Assert.False(string.IsNullOrWhiteSpace(name)));
        Assert.True(
            names.Any(n => n.Equals("File Explorer", StringComparison.OrdinalIgnoreCase)
                || n.Equals("Notepad", StringComparison.OrdinalIgnoreCase)
                || n.Equals("Calculator", StringComparison.OrdinalIgnoreCase)),
            "no packaged application was listed: " + string.Join(", ", names.Take(10)));
    }
}
