using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public sealed class AndroidToolsTests
{
    [Fact]
    public void ParseEmulatorSerials_KeepsOnlineEmulators_DropsPhysicalAndOffline()
    {
        const string output = """
            List of devices attached
            emulator-5554	device
            emulator-5556	offline
            R58M12ABCDE	device
            emulator-5558	device

            """;

        Assert.Equal(
            ["emulator-5554", "emulator-5558"],
            AndroidTools.ParseEmulatorSerials(output.ReplaceLineEndings("\n")));
    }

    [Fact]
    public void ParseEmulatorSerials_EmptyOrHeaderOnly_IsEmpty()
    {
        Assert.Empty(AndroidTools.ParseEmulatorSerials(""));
        Assert.Empty(AndroidTools.ParseEmulatorSerials("List of devices attached\n"));
    }

    [Fact]
    public void CreateIfAvailable_HonorsTheSettingSwitch()
    {
        // Off always yields nothing, whether or not adb exists on this machine.
        Assert.Empty(AndroidTools.CreateIfAvailable(new UiSettings { AndroidToolsEnabled = false }));

        // On: either adb is present and the one tool the reference's control
        // does not cover appears, or adb is absent and nothing does. The other
        // four are now actions of that tool — see AndroidEmulatorTests.
        var tools = AndroidTools.CreateIfAvailable(new UiSettings { AndroidToolsEnabled = true });
        if (AndroidTools.FindAdb() is not null)
        {
            Assert.Equal(["android_logcat"], tools.Select(static t => t.Name));
        }
        else
        {
            Assert.Empty(tools);
        }
    }
}
