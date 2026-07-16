using System.Text.Json;
using OneVolume.Core.Leveling;

namespace OneVolume.Core.Settings;

/// <summary>
/// Persisted user settings (JSON in %LocalAppData%\OneVolume). Everything maps onto
/// <see cref="LevelerSettings"/> plus app-level toggles. Corrupt or missing settings fall
/// back to defaults — the app must always start.
/// </summary>
public sealed class AppSettings
{
    public bool LevelingEnabled { get; set; } = true;

    public float TargetLevel { get; set; } = 0.25f;

    public bool NightMode { get; set; }

    public List<string> ExcludedProcesses { get; set; } = [];

    public bool StartWithWindows { get; set; }

    /// <summary>
    /// Hide to tray on launch. Off by default so a manual first launch shows the window;
    /// the Start-with-Windows registry entry passes --minimized explicitly.
    /// </summary>
    public bool StartMinimized { get; set; }

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OneVolume");

    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // fall through to defaults
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // best effort — losing a save must never crash the app
        }
    }

    /// <summary>Copies the persisted values onto a live engine settings object.</summary>
    public void ApplyTo(LevelerSettings settings)
    {
        settings.TargetLevel = Math.Clamp(TargetLevel, 0.05f, 0.6f);
        settings.NightMode = NightMode;
        settings.ExcludedProcesses.Clear();
        foreach (string name in ExcludedProcesses)
        {
            settings.ExcludedProcesses.Add(name);
        }
    }
}
