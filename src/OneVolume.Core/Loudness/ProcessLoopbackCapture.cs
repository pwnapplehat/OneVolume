using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OneVolume.Core.Loudness;

/// <summary>
/// Per-process loopback capture (Windows 10 2004+): taps ONE process tree's audio
/// stream — post-session-volume, i.e. exactly what the user hears from that app
/// (verified empirically) — and feeds it into a <see cref="Bs1770Meter"/> on a
/// background thread. If activation fails (older Windows, exotic session), the owner
/// falls back to peak metering; capture is an enhancement, never a requirement.
/// Capture is read-only: it never modifies, re-routes, or adds latency to playback.
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    private const string VirtualDevice = @"VAD\Process_Loopback";
    private const int ActivationTypeProcessLoopback = 1;
    private const int LoopbackModeIncludeTree = 0;
    private const uint StreamFlagsLoopback = 0x00020000;
    private const uint StreamFlagsEventCallback = 0x00040000;
    private const int SampleRate = 48000;
    private const int Channels = 2;

    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();

    public ProcessLoopbackCapture(int processId)
    {
        ProcessId = processId;
        Meter = new Bs1770Meter(SampleRate, Channels);
        _thread = new Thread(() => CaptureLoop(processId))
        {
            IsBackground = true,
            Name = $"OneVolume.Capture.{processId}",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    public int ProcessId { get; }

    public Bs1770Meter Meter { get; }

    /// <summary>True once the capture stream is delivering samples.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>True when activation failed — owner should use the peak fallback.</summary>
    public bool Failed { get; private set; }

    private void CaptureLoop(int pid)
    {
        try
        {
            RunCapture(pid, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch
        {
            Failed = true; // peak fallback takes over
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void RunCapture(int pid, CancellationToken ct)
    {
        var activationParams = new AudioClientActivationParams
        {
            ActivationType = ActivationTypeProcessLoopback,
            TargetProcessId = (uint)pid,
            ProcessLoopbackMode = LoopbackModeIncludeTree,
        };

        int structSize = Marshal.SizeOf<AudioClientActivationParams>();
        IntPtr paramsPtr = Marshal.AllocHGlobal(structSize);
        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);
            var propvariant = new PropVariant
            {
                vt = 65, // VT_BLOB
                blobSize = (uint)structSize,
                blobData = paramsPtr,
            };

            var handler = new ActivateCompletionHandler();
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync(
                VirtualDevice, typeof(IAudioClient).GUID, ref propvariant, handler, out _));
            if (!handler.Done.WaitOne(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("audio interface activation timed out");
            }

            Marshal.ThrowExceptionForHR(handler.ActivateHr);
            var client = (IAudioClient)handler.Interface!;

            var format = new WaveFormatEx
            {
                FormatTag = 3, // IEEE float
                Channels = Channels,
                SamplesPerSec = SampleRate,
                BitsPerSample = 32,
                BlockAlign = Channels * 4,
                AvgBytesPerSec = SampleRate * Channels * 4,
                Size = 0,
            };
            IntPtr formatPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
            try
            {
                Marshal.StructureToPtr(format, formatPtr, false);
                Marshal.ThrowExceptionForHR(client.Initialize(
                    0 /* shared */, StreamFlagsLoopback | StreamFlagsEventCallback,
                    2_000_000 /* 200 ms buffer */, 0, formatPtr, IntPtr.Zero));
            }
            finally
            {
                Marshal.FreeHGlobal(formatPtr);
            }

            using var packetReady = new AutoResetEvent(false);
            Marshal.ThrowExceptionForHR(client.SetEventHandle(packetReady.SafeWaitHandle.DangerousGetHandle()));

            Guid captureGuid = typeof(IAudioCaptureClient).GUID;
            Marshal.ThrowExceptionForHR(client.GetService(ref captureGuid, out object captureObj));
            var capture = (IAudioCaptureClient)captureObj;

            Marshal.ThrowExceptionForHR(client.Start());
            IsRunning = true;

            float[] buffer = new float[SampleRate]; // ½ s of stereo floats headroom
            while (!ct.IsCancellationRequested)
            {
                packetReady.WaitOne(200);
                while (!ct.IsCancellationRequested)
                {
                    Marshal.ThrowExceptionForHR(capture.GetNextPacketSize(out uint packetFrames));
                    if (packetFrames == 0)
                    {
                        break;
                    }

                    Marshal.ThrowExceptionForHR(capture.GetBuffer(
                        out IntPtr data, out uint frames, out uint flags, out _, out _));
                    int floats = (int)frames * Channels;
                    if (floats > buffer.Length)
                    {
                        buffer = new float[floats];
                    }

                    const uint SilentFlag = 0x2; // AUDCLNT_BUFFERFLAGS_SILENT
                    if ((flags & SilentFlag) != 0)
                    {
                        Array.Clear(buffer, 0, floats);
                    }
                    else
                    {
                        Marshal.Copy(data, buffer, 0, floats);
                    }

                    Meter.Process(buffer.AsSpan(0, floats));
                    Marshal.ThrowExceptionForHR(capture.ReleaseBuffer(frames));
                }
            }

            client.Stop();
        }
        finally
        {
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (!_thread.Join(TimeSpan.FromSeconds(2)))
        {
            // Background thread — dies with the process if the join times out.
        }

        _cts.Dispose();
    }

    // ------------------------------------------------------------------ interop

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        ref PropVariant activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort r1;
        public ushort r2;
        public ushort r3;
        public uint blobSize;
        public IntPtr blobData;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object? activatedInterface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferSize);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint numFramesRead, out uint flags, out long devicePosition, out long qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
    }

    private sealed class ActivateCompletionHandler : IActivateAudioInterfaceCompletionHandler
    {
        public ManualResetEvent Done { get; } = new(false);

        public int ActivateHr { get; private set; }

        public object? Interface { get; private set; }

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            activateOperation.GetActivateResult(out int hr, out object? activated);
            ActivateHr = hr;
            Interface = activated;
            Done.Set();
        }
    }
}
