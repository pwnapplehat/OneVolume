using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Microsoft.Win32;
using OneVolume.Core.Audio;
using OneVolume.Core.Leveling;
using OneVolume.Core.Settings;

namespace OneVolume.App.ViewModels;

/// <summary>
/// Row in the live sessions list (one per audio session, keyed by session id).
/// The slider is a real mixer control: dragging it sets the app's volume through the
/// engine (which pins the session — the user's choice wins over the algorithm).
/// </summary>
public sealed class SessionRow : INotifyPropertyChanged
{
    private readonly Action<SessionRow, float> _onUserVolume;
    private float _heard;
    private float _volume;
    private string _status = "";
    private bool _updatingFromEngine;

    public SessionRow(Action<SessionRow, float> onUserVolume) => _onUserVolume = onUserVolume;

    public required string SessionId { get; init; }

    public required string ProcessName { get; init; }

    public required int ProcessId { get; init; }

    public float Heard
    {
        get => _heard;
        private set { _heard = value; OnChanged(); OnChanged(nameof(HeardPercent)); }
    }

    public string Status
    {
        get => _status;
        private set
        {
            if (_status != value)
            {
                _status = value;
                OnChanged();
            }
        }
    }

    /// <summary>Slider binding, 0..100. Setter only fires the mixer action for user edits.</summary>
    public double VolumePercent
    {
        get => _volume * 100;
        set
        {
            float clamped = (float)Math.Clamp(value, 0, 100) / 100f;
            if (Math.Abs(clamped - _volume) < 0.004f)
            {
                return;
            }

            _volume = clamped;
            OnChanged();
            OnChanged(nameof(VolumeText));
            if (!_updatingFromEngine)
            {
                _onUserVolume(this, clamped);
            }
        }
    }

    public double HeardPercent => Math.Min(100, Heard * 260);

    public string VolumeText => $"{_volume:P0}";

    /// <summary>Engine → UI mirror; never triggers the mixer action.</summary>
    public void UpdateFromEngine(float volume, float heard, string status)
    {
        _updatingFromEngine = true;
        try
        {
            VolumePercent = volume * 100;
            Heard = heard;
            Status = status;
        }
        finally
        {
            _updatingFromEngine = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One per-app rule row in the rules editor.</summary>
public sealed class RuleRow : INotifyPropertyChanged
{
    public static readonly string[] KindOptions = ["Level automatically", "Fixed volume", "Never touch"];

    private readonly Action<RuleRow> _onEdited;
    private string _kindOption;
    private double _fixedPercent;
    private bool _initializing = true;

    public RuleRow(AppRule rule, Action<RuleRow> onEdited)
    {
        _onEdited = onEdited;
        ProcessName = rule.ProcessName;
        _kindOption = rule.Kind switch
        {
            RuleKind.Fixed => KindOptions[1],
            RuleKind.Exclude => KindOptions[2],
            _ => KindOptions[0],
        };
        _fixedPercent = Math.Round(rule.SafeFixedVolume * 100);
        _initializing = false;
    }

    public string ProcessName { get; }

    public string[] Options => KindOptions;

    public string KindOption
    {
        get => _kindOption;
        set
        {
            if (_kindOption == value)
            {
                return;
            }

            _kindOption = value;
            OnChanged();
            OnChanged(nameof(IsFixed));
            NotifyEdited();
        }
    }

    public bool IsFixed => _kindOption == KindOptions[1];

    public double FixedPercent
    {
        get => _fixedPercent;
        set
        {
            double clamped = Math.Round(Math.Clamp(value, 0, 100));
            if (Math.Abs(clamped - _fixedPercent) < 0.5)
            {
                return;
            }

            _fixedPercent = clamped;
            OnChanged();
            OnChanged(nameof(FixedPercentText));
            NotifyEdited();
        }
    }

    public string FixedPercentText => $"{_fixedPercent:0}%";

    public RuleKind Kind => _kindOption == KindOptions[1] ? RuleKind.Fixed
        : _kindOption == KindOptions[2] ? RuleKind.Exclude
        : RuleKind.Level;

    public AppRule ToRule() => new(ProcessName, Kind, (float)(_fixedPercent / 100.0));

    private void NotifyEdited()
    {
        if (!_initializing)
        {
            _onEdited(this);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Hosts the leveling engine on a 20 Hz dispatcher timer and exposes everything the
/// window binds to: the master toggle, target dial, live per-app mixer, and the per-app
/// rules (fixed volume / never touch / level), which apply the moment an app's audio
/// session appears — including apps that weren't running when the rule was created.
/// Turning leveling off (or exiting) restores every session volume the engine ever
/// touched — OneVolume must leave no trace when disabled.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly AppSettings _appSettings;
    private readonly LevelerSettings _levelerSettings = new();
    private readonly WasapiSessionSource _source = new();
    private readonly LevelingEngine _engine;
    private readonly DispatcherTimer _timer;
    private string _lastDeviceName = "";

    public MainViewModel()
    {
        _appSettings = AppSettings.Load();
        _appSettings.ApplyTo(_levelerSettings);
        _engine = new LevelingEngine(_source, _levelerSettings, new VolumeJournal());

        // If a previous run crashed while apps were attenuated, put their volumes back
        // before doing anything else — an attenuated volume must never become permanent.
        _engine.RecoverOrphanedVolumes();

        foreach (AppRule rule in _levelerSettings.Rules.Values
                     .OrderBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase))
        {
            Rules.Add(new RuleRow(rule, OnRuleEdited));
        }

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _timer.Tick += (_, _) => TickOnce();
        if (_appSettings.LevelingEnabled)
        {
            _timer.Start();
        }
    }

    public ObservableCollection<SessionRow> Sessions { get; } = [];

    public ObservableCollection<RuleRow> Rules { get; } = [];

    public bool StartMinimizedPreferred => _appSettings.StartMinimized;

    public bool LevelingEnabled
    {
        get => _appSettings.LevelingEnabled;
        set
        {
            if (_appSettings.LevelingEnabled == value)
            {
                return;
            }

            _appSettings.LevelingEnabled = value;
            if (value)
            {
                // Heal any app that was left attenuated while we were paused (it may have
                // been closed during a previous leveling run and reopened just now).
                _engine.RecoverOrphanedVolumes();
                _timer.Start();
            }
            else
            {
                _timer.Stop();
                _engine.RestoreOriginalVolumes(); // leave the system exactly as found
                Sessions.Clear(); // don't show frozen "leveling" rows while paused
            }

            Save();
            OnChanged();
            OnChanged(nameof(StatusText));
        }
    }

    /// <summary>Slider value 5..60 mapped to target heard level 0.05..0.60.</summary>
    public double TargetPercent
    {
        get => _levelerSettings.TargetLevel * 100;
        set
        {
            float clamped = (float)Math.Clamp(value, 5, 60) / 100f;
            if (Math.Abs(clamped - _levelerSettings.TargetLevel) < 0.001f)
            {
                return;
            }

            _levelerSettings.TargetLevel = clamped;
            _appSettings.TargetLevel = clamped;
            Save();
            OnChanged();
        }
    }

    public bool NightMode
    {
        get => _levelerSettings.NightMode;
        set
        {
            _levelerSettings.NightMode = value;
            _appSettings.NightMode = value;
            Save();
            OnChanged();
            OnChanged(nameof(StatusText));
        }
    }

    public bool StartWithWindows
    {
        get => _appSettings.StartWithWindows;
        set
        {
            _appSettings.StartWithWindows = value;
            ApplyStartWithWindows(value);
            Save();
            OnChanged();
        }
    }

    public string DeviceName => _source.DeviceName;

    public string StatusText => !LevelingEnabled
        ? "Paused — app volumes restored to your own settings."
        : NightMode
            ? "Active (night mode) — quieter target, tighter range."
            : "Active — leveling, per-app rules, and blast protection.";

    public bool HasNoRules => Rules.Count == 0;

    // ------------------------------------------------------------------ rules

    /// <summary>Adds (or focuses) a rule for a process name; default kind = Fixed 50%.</summary>
    public void AddRule(string processName, RuleKind kind = RuleKind.Fixed, float fixedVolume = 0.5f)
    {
        string name = processName.Trim();
        if (name.Length == 0)
        {
            return;
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        if (Rules.Any(r => string.Equals(r.ProcessName, name, StringComparison.OrdinalIgnoreCase)))
        {
            return; // one rule per app
        }

        var rule = new AppRule(name, kind, fixedVolume);
        _levelerSettings.Rules[name] = rule;
        Rules.Add(new RuleRow(rule, OnRuleEdited));
        _engine.UnpinProcess(name);
        PersistRules();
        OnChanged(nameof(HasNoRules));
    }

    public void RemoveRule(RuleRow row)
    {
        Rules.Remove(row);
        _levelerSettings.Rules.Remove(row.ProcessName);
        _engine.UnpinProcess(row.ProcessName); // back to plain leveling right away
        PersistRules();
        OnChanged(nameof(HasNoRules));
    }

    private void OnRuleEdited(RuleRow row)
    {
        _levelerSettings.Rules[row.ProcessName] = row.ToRule();
        _engine.UnpinProcess(row.ProcessName); // re-apply the new rule immediately
        PersistRules();
    }

    private void PersistRules()
    {
        _appSettings.CaptureRules(_levelerSettings);
        Save();
    }

    /// <summary>Apps for the picker: everything installed plus whatever is playing now.</summary>
    public List<Services.InstalledApp> GetPickerApps()
    {
        List<Services.InstalledApp> apps = Services.InstalledApps.Enumerate();
        var known = new HashSet<string>(apps.Select(a => a.ProcessName), StringComparer.OrdinalIgnoreCase);
        foreach (SessionRow row in Sessions)
        {
            if (known.Add(row.ProcessName))
            {
                apps.Add(new Services.InstalledApp(row.ProcessName + " (playing now)", row.ProcessName));
            }
        }

        return [.. apps.OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)];
    }

    // ------------------------------------------------------------------ mixer

    private void OnUserVolume(SessionRow row, float volume)
        => _engine.SetSessionVolume(row.SessionId, volume);

    private void TickOnce()
    {
        _engine.Tick();

        // Mirror engine state into the bound rows, keyed by session id (a process can own
        // several sessions — keying by PID would create duplicate rows and then crash the
        // dictionary build on the following tick). Update-in-place to avoid list churn.
        IReadOnlyList<SessionState> states = _engine.LastStates;
        var byId = new Dictionary<string, SessionRow>(Sessions.Count);
        foreach (SessionRow existing in Sessions)
        {
            byId[existing.SessionId] = existing;
        }

        var seen = new HashSet<string>();
        foreach (SessionState s in states)
        {
            if (!seen.Add(s.Id))
            {
                continue; // defensive: never two rows for one session
            }

            if (!byId.TryGetValue(s.Id, out SessionRow? row))
            {
                row = new SessionRow(OnUserVolume) { SessionId = s.Id, ProcessName = s.ProcessName, ProcessId = s.ProcessId };
                byId[s.Id] = row;
                Sessions.Add(row);
            }

            RuleKind kind = _levelerSettings.ResolveRule(s.ProcessName).Kind;
            string status = s.Excluded ? "excluded"
                : s.Pinned && kind == RuleKind.Fixed ? "fixed"
                : s.Pinned ? "manual"
                : s.Gated ? "silent"
                : s.Correcting ? "leveling"
                : "steady";
            row.UpdateFromEngine(s.Volume, s.HeardLevel, status);
        }

        for (int i = Sessions.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Sessions[i].SessionId))
            {
                Sessions.RemoveAt(i);
            }
        }

        // Device name changes only when the default output actually switches.
        string device = _source.DeviceName;
        if (device != _lastDeviceName)
        {
            _lastDeviceName = device;
            OnChanged(nameof(DeviceName));
        }
    }

    private static void ApplyStartWithWindows(bool enable)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null)
            {
                return;
            }

            if (enable)
            {
                key.SetValue("OneVolume", $"\"{Environment.ProcessPath}\" --minimized");
            }
            else
            {
                key.DeleteValue("OneVolume", throwOnMissingValue: false);
            }
        }
        catch
        {
            // Non-fatal: the toggle simply won't stick.
        }
    }

    private void Save() => _appSettings.Save();

    public void Dispose()
    {
        _timer.Stop();
        if (LevelingEnabled)
        {
            _engine.RestoreOriginalVolumes();
        }

        _source.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
