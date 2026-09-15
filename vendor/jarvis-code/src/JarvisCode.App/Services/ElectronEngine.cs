using System.Windows.Threading;

namespace JarvisCode.App.Services;

/// <summary>
/// The app's single engine process, shared by every surface that renders web
/// content.
///
/// Each surface gets its own <see cref="ElectronPaneSession"/> — its own window
/// in the engine, reparented wherever that surface lives — but they all speak to
/// one <see cref="ElectronPaneHost"/>, so the app runs one Chromium rather than
/// one per surface. The engine starts on the first surface that needs it and
/// ends when the app does: closing the pipe is its shutdown signal, so nothing
/// is left running behind the window that asked for it.
/// </summary>
public static class ElectronEngine
{
    private static readonly Lock Gate = new();
    private static ElectronPaneHost? _host;
    private static string? _userDataDirectory;

    /// <summary>
    /// Where the engine keeps its profile. Set once, before the first surface
    /// asks for a session; a later call with a different directory is ignored,
    /// because the running engine already holds the first one open.
    /// </summary>
    public static void UseProfile(string userDataDirectory)
    {
        lock (Gate)
        {
            _userDataDirectory ??= userDataDirectory;
        }
    }

    /// <summary>True once a surface has started the engine.</summary>
    public static bool IsRunning
    {
        get
        {
            lock (Gate)
            {
                return _host?.IsRunning == true;
            }
        }
    }

    /// <summary>The engine's Chromium, once it has connected. Null before that.</summary>
    public static string? ChromiumVersion
    {
        get
        {
            lock (Gate)
            {
                return _host?.ChromiumVersion;
            }
        }
    }

    /// <summary>A session of this surface's own, over the shared engine process.</summary>
    public static ElectronPaneSession CreateSession(Dispatcher dispatcher, string? storagePartition = null)
    {
        lock (Gate)
        {
            _host ??= new ElectronPaneHost(
                new ElectronRuntime(),
                ElectronPaneHost.DefaultAppDirectory,
                _userDataDirectory ?? DefaultProfileDirectory());

            return new ElectronPaneSession(_host, dispatcher, ownsHost: false, storagePartition: storagePartition);
        }
    }

    private static string DefaultProfileDirectory() =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisCode", "engine");

    /// <summary>
    /// Ends the engine and every window in it. Called when the app is going
    /// down; a surface closing its own session must not reach this, or the next
    /// surface would find the engine gone.
    /// </summary>
    public static async Task ShutdownAsync()
    {
        ElectronPaneHost? host;
        lock (Gate)
        {
            host = _host;
            _host = null;
        }

        if (host is not null)
        {
            await host.DisposeAsync().ConfigureAwait(false);
        }
    }
}
