using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Microsoft.Win32;
using OneVolume.Core.Audio;
using OneVolume.Core.Leveling;
using OneVolume.Core.Settings;

namespace OneVolume.App.ViewModels;

/// <summary>Row in the live sessions list (one per audio session, keyed by session id).</summary>
public sealed class SessionRow : INotifyPropertyChanged
{
    private float _heard;
    private float _volume;
    private string _status = "";

    public required string SessionId { get; init; }

    public required string ProcessName { get; init; }

    public required int ProcessId { get; init; }

    public float Heard
    {
        get => _heard;
        set { _heard = value; OnChanged(); OnChanged(nameof(HeardPercent)); }
    }

    public float Volume
    {
        get => _volume;
        set { _volume = value; OnChanged(); OnChanged(nameof(VolumeText)); }
    }

    public string Status
    {
        get => _status;
        set { _status = value; OnChanged(); }
    }

    public double HeardPercent => Math.Min(100, Heard * 260);

    public string VolumeText => $"{Volume:P0}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Hosts the leveling engine on a 20 Hz dispatcher timer and exposes everything the
/// window binds to. Turning leveling off (or exiting) restores every session volume
/// the engine ever touched — OneVolume must leave no trace when disabled.
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

    public string ExcludedText
    {
        get => string.Join(", ", _levelerSettings.ExcludedProcesses.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        set
        {
            _levelerSettings.ExcludedProcesses.Clear();
            foreach (string name in ProcessNames.Parse(value))
            {
                _levelerSettings.ExcludedProcesses.Add(name);
            }

            _appSettings.ExcludedProcesses = [.. _levelerSettings.ExcludedProcesses];
            Save();
            OnChanged();
        }
    }

    public string DeviceName => _source.DeviceName;

    public string StatusText => !LevelingEnabled
        ? "Paused — app volumes restored to your own settings."
        : NightMode
            ? "Leveling (night mode) — quieter target, tighter range."
            : "Leveling — loud apps are eased down to your target.";

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
                row = new SessionRow { SessionId = s.Id, ProcessName = s.ProcessName, ProcessId = s.ProcessId };
                byId[s.Id] = row;
                Sessions.Add(row);
            }

            row.Heard = s.HeardLevel;
            row.Volume = s.Volume;
            row.Status = s.Excluded ? "excluded"
                : s.Pinned ? "manual"
                : s.Gated ? "silent"
                : s.Correcting ? "leveling"
                : "steady";
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
