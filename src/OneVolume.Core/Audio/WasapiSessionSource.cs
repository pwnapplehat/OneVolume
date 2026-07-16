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
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _device;
    private string _deviceId = "";

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
        }

        public string Id { get; }

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
