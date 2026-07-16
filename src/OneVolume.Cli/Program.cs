using System.Diagnostics;
using System.Globalization;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using OneVolume.Core.Audio;
using OneVolume.Core.Leveling;

// ============================================================================
// OneVolume CLI — diagnostics and the real-hardware E2E harness.
//
//   sessions            list current app audio sessions (read-only)
//   tone <hz> <amp> <s> [child mode] play a sine tone in this process
//   e2e [secs]          spawn a loud + a quiet tone process and run the REAL
//                       LevelingEngine over them (other apps' sessions are
//                       filtered out — the test never touches real apps),
//                       then restore volumes and report a verdict.
// ============================================================================

return args.FirstOrDefault()?.ToLowerInvariant() switch
{
    "sessions" => ListSessions(),
    "tone" => PlayTone(
        float.Parse(args[1], CultureInfo.InvariantCulture),
        float.Parse(args[2], CultureInfo.InvariantCulture),
        int.Parse(args[3], CultureInfo.InvariantCulture)),
    "e2e" => RunE2E(args.Length > 1 ? int.Parse(args[1]) : 12),
    _ => Help(),
};

static int Help()
{
    Console.WriteLine("""
        OneVolume CLI
          onevolume-cli sessions        list app audio sessions (read-only)
          onevolume-cli e2e [seconds]   real-hardware leveling test using its own tone processes
        """);
    return 0;
}

static int ListSessions()
{
    using var source = new WasapiSessionSource();
    Console.WriteLine($"Output device: {source.DeviceName}\n");
    Console.WriteLine($"{"PID",7}  {"Vol",5}  {"Peak",6}  Process");
    foreach (IAudioSession s in source.GetSessions())
    {
        Console.WriteLine($"{s.ProcessId,7}  {s.Volume,5:0.00}  {s.RawPeak,6:0.000}  {s.ProcessName}");
    }

    return 0;
}

static int PlayTone(float frequency, float amplitude, int seconds)
{
    var signal = new SignalGenerator(44100, 2)
    {
        Gain = amplitude,
        Frequency = frequency,
        Type = SignalGeneratorType.Sin,
    };
    using var output = new WaveOutEvent();
    output.Init(signal);
    output.Play();
    Thread.Sleep(TimeSpan.FromSeconds(seconds));
    return 0;
}

static int RunE2E(int seconds)
{
    string exe = Environment.ProcessPath!;
    int toneSecs = seconds + 8;

    // Both tones are clearly ABOVE the target level, ~6 dB apart, so the engine must pull
    // both down and they must converge at the target. (A below-target app is deliberately
    // left alone — attenuation-only — so it wouldn't demonstrate convergence.)
    using Process loud = Process.Start(new ProcessStartInfo(exe, $"tone 440 0.90 {toneSecs}") { UseShellExecute = false })!;
    using Process quiet = Process.Start(new ProcessStartInfo(exe, $"tone 660 0.45 {toneSecs}") { UseShellExecute = false })!;
    int[] pids = [loud.Id, quiet.Id];
    Console.WriteLine($"spawned tone processes: louder={loud.Id} (0.90) loud={quiet.Id} (0.45)");

    using var raw = new WasapiSessionSource();
    using var filtered = new PidFilteredSource(raw, pids);
    var settings = new LevelerSettings();
    var engine = new LevelingEngine(filtered, settings);

    // Wait for both children's sessions to appear.
    for (int i = 0; i < 60 && filtered.GetSessions().Count < 2; i++)
    {
        Thread.Sleep(250);
    }

    if (filtered.GetSessions().Count < 2)
    {
        Console.Error.WriteLine("E2E FAIL: tone sessions did not appear");
        return 1;
    }

    Console.WriteLine($"device: {raw.DeviceName}; running engine at 20 Hz for {seconds}s (only touching its own tone processes)\n");
    var clock = Stopwatch.StartNew();
    long nextLog = 0;
    while (clock.Elapsed.TotalSeconds < seconds)
    {
        engine.Tick();
        if (clock.ElapsedMilliseconds >= nextLog)
        {
            nextLog += 2000;
            string row = string.Join("   ", engine.LastStates.Select(s =>
                $"{(s.ProcessId == loud.Id ? "loud" : "quiet")} vol={s.Volume:0.00} heard={s.HeardLevel:0.000}"));
            Console.WriteLine($"t={clock.Elapsed.TotalSeconds,4:0.0}s  {row}");
        }

        Thread.Sleep(50);
    }

    // Verdict: heard levels should be within the deadband of each other.
    SessionState[] states = engine.LastStates.ToArray();
    float loudHeard = states.First(s => s.ProcessId == loud.Id).HeardLevel;
    float quietHeard = states.First(s => s.ProcessId == quiet.Id).HeardLevel;
    double gapDb = Math.Abs(20 * Math.Log10(Math.Max(loudHeard, 1e-6) / Math.Max(quietHeard, 1e-6)));

    Console.WriteLine($"\nfinal heard gap: {gapDb:0.0} dB (started ≈ 16.9 dB)");

    engine.RestoreOriginalVolumes();
    Console.WriteLine("volumes restored to original values");

    try { loud.Kill(); } catch { }
    try { quiet.Kill(); } catch { }

    bool pass = gapDb < 4.0;
    Console.WriteLine(pass ? "\nE2E PASS: real-hardware leveling through the engine works."
                           : "\nE2E FAIL: convergence outside 4 dB.");
    return pass ? 0 : 2;
}

/// <summary>Test-only wrapper: exposes just the sessions whose PID we spawned ourselves.</summary>
internal sealed class PidFilteredSource : IAudioSessionSource
{
    private readonly IAudioSessionSource _inner;
    private readonly HashSet<int> _pids;

    public PidFilteredSource(IAudioSessionSource inner, IEnumerable<int> pids)
    {
        _inner = inner;
        _pids = [.. pids];
    }

    public string DeviceName => _inner.DeviceName;

    public IReadOnlyList<IAudioSession> GetSessions()
        => _inner.GetSessions().Where(s => _pids.Contains(s.ProcessId)).ToList();

    public void Dispose()
    {
        // Inner source's lifetime is owned by the caller.
    }
}
