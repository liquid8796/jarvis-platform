namespace JarvisCode.Core.Settings;

/// <summary>Loads and saves <see cref="AppSettings"/>, transparently protecting API keys at rest.</summary>
public interface ISettingsStore
{
    AppSettings Load();

    void Save(AppSettings settings);
}
