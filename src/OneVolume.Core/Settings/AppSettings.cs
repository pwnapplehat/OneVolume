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
    /// <summary>One persisted per-app rule (see <see cref="AppRule"/>).</summary>
    public sealed class RuleModel
    {
        public string ProcessName { get; set; } = "";
        public string Kind { get; set; } = nameof(RuleKind.Level);
        public float FixedVolume { get; set; } = 1.0f;
    }

    public bool LevelingEnabled { get; set; } = true;

    public float TargetLevel { get; set; } = 0.25f;

    public bool NightMode { get; set; }

    /// <summary>Legacy exclusion list (pre-1.1). Migrated into <see cref="Rules"/> on load.</summary>
    public List<string> ExcludedProcesses { get; set; } = [];

    /// <summary>Per-app rules: fixed volumes, exclusions, explicit leveling.</summary>
    public List<RuleModel> Rules { get; set; } = [];

    public bool StartWithWindows { get; set; }

    /// <summary>
    /// Hide to tray on launch. Off by default so a manual first launch shows the window;
    /// the Start-with-Windows registry entry passes --minimized explicitly.
    /// </summary>
    public bool StartMinimized { get; set; }

    /// <summary>
    /// When true (default), one notify-only check for a newer release runs at startup.
    /// OneVolume never downloads or replaces itself — the banner links to the release page.
    /// </summary>
    public bool UpdateCheckEnabled { get; set; } = true;

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

        settings.Rules.Clear();
        foreach (RuleModel model in Rules)
        {
            if (string.IsNullOrWhiteSpace(model.ProcessName))
            {
                continue;
            }

            RuleKind kind = Enum.TryParse(model.Kind, ignoreCase: true, out RuleKind parsed)
                ? parsed
                : RuleKind.Level;
            settings.Rules[model.ProcessName.Trim()] =
                new AppRule(model.ProcessName.Trim(), kind, Math.Clamp(model.FixedVolume, 0f, 1f));
        }

        // Migration: pre-1.1 settings carried a plain exclusion list. Fold it into the
        // rules (existing explicit rules win) so old installs keep their behavior.
        settings.ExcludedProcesses.Clear();
        foreach (string name in ExcludedProcesses)
        {
            if (!string.IsNullOrWhiteSpace(name) && !settings.Rules.ContainsKey(name.Trim()))
            {
                settings.Rules[name.Trim()] = new AppRule(name.Trim(), RuleKind.Exclude);
            }
        }
    }

    /// <summary>Copies the live rules back into the persisted model (before Save).</summary>
    public void CaptureRules(LevelerSettings settings)
    {
        Rules = [.. settings.Rules.Values
            .OrderBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(r => new RuleModel
            {
                ProcessName = r.ProcessName,
                Kind = r.Kind.ToString(),
                FixedVolume = r.SafeFixedVolume,
            })];
        ExcludedProcesses = []; // fully migrated — the legacy list is now the rules
    }
}
