using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace OneVolume.Core.Audio;

/// <summary>
/// Live sessions on the current default render device via WASAPI (NAudio wrappers).
/// Re-resolves the default device when it changes (headphones plugged in, Bluetooth
/// connects…) so leveling follows the audio the user actually hears. System sounds
/// (PID 0) are ignored — Windows chimes are not something to level.
/// </summary>
public sealed class WasapiSessionSource : IAudioSessionSource
{
    /// <summary>
    /// How often the default device is re-resolved. Sessions are refreshed on every call
    /// (cheap, and new apps must be caught fast for blast protection), but re-creating the
    /// default MMDevice 20× a second is pointless COM churn — a 1 s check is plenty for
    /// "user plugged in headphones".
    /// </summary>
    private static readonly TimeSpan DeviceCheckInterval = TimeSpan.FromSeconds(1);

    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _device;
    private string _deviceId = "";
    private DateTime _lastDeviceCheckUtc = DateTime.MinValue;

    public string DeviceName => _device?.FriendlyName ?? "(no output device)";

    public IReadOnlyList<IAudioSession> GetSessions()
    {
        var result = new List<IAudioSession>();
        try
        {
            EnsureDevice();
            if (_device is null)
            {
                return result;
            }

            AudioSessionManager manager = _device.AudioSessionManager;
            manager.RefreshSessions();
            SessionCollection sessions = manager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                AudioSessionControl control = sessions[i];
                int pid = (int)control.GetProcessID;
                if (pid == 0)
                {
                    continue; // system sounds session
                }

                result.Add(new WasapiSession(control, _deviceId, pid));
            }
        }
        catch
        {
            // Device disappeared mid-enumeration (unplugged) — return what we have;
            // the next tick re-resolves the default device.
            _device = null;
        }

        return result;
    }

    private void EnsureDevice()
    {
        // Always re-resolve when we have no device (startup, or it was unplugged);
        // otherwise only re-check on the throttle interval.
        DateTime now = DateTime.UtcNow;
        if (_device is not null && now - _lastDeviceCheckUtc < DeviceCheckInterval)
        {
            return;
        }

        _lastDeviceCheckUtc = now;
        MMDevice fresh = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        if (_device is null || fresh.ID != _deviceId)
        {
            _device?.Dispose();
            _device = fresh;
            _deviceId = fresh.ID;
        }
        else
        {
            fresh.Dispose();
        }
    }

    public void Dispose()
    {
        _device?.Dispose();
        _enumerator.Dispose();
    }

    private sealed class WasapiSession : IAudioSession
    {
        private readonly AudioSessionControl _control;
        private string? _processName;

        public WasapiSession(AudioSessionControl control, string deviceId, int pid)
        {
            _control = control;
            ProcessId = pid;
            Id = deviceId + "|" + control.GetSessionInstanceIdentifier;

            // Session identifier (no instance suffix): the identity Windows itself keys
            // persisted per-app volume on — stable across app restarts on this device.
            StableId = deviceId + "|" + control.GetSessionIdentifier;
        }

        public string Id { get; }

        public string StableId { get; }

        public int ProcessId { get; }

        public string ProcessName
        {
            get
            {
                if (_processName is null)
                {
                    try
                    {
                        using Process p = Process.GetProcessById(ProcessId);
                        _processName = p.ProcessName;
                    }
                    catch
                    {
                        _processName = "pid:" + ProcessId;
                    }
                }

                return _processName;
            }
        }

        public float RawPeak => _control.AudioMeterInformation.MasterPeakValue;

        public float Volume
        {
            get => _control.SimpleAudioVolume.Volume;
            set => _control.SimpleAudioVolume.Volume = value;
        }

        public bool IsAlive
        {
            get
            {
                try
                {
                    return _control.State != AudioSessionState.AudioSessionStateExpired;
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}
