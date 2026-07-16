using OneVolume.Core.Leveling;

namespace OneVolume.Core.Tests;

/// <summary>
/// Guards the crash-recovery path: if OneVolume dies while an app is attenuated, the
/// journal on disk must bring the app's volume back on the next start — otherwise the
/// attenuated value silently becomes the app's permanent Windows volume.
/// </summary>
public class VolumeJournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onevolume-tests", Guid.NewGuid().ToString("N"));

    private string JournalPath => Path.Combine(_dir, "journal.json");

    public VolumeJournalTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void Journal_round_trips_entries_across_instances()
    {
        var first = new VolumeJournal(JournalPath);
        first.Record("dev|app1", 0.8f);
        first.Record("dev|app2", 1.0f);

        var second = new VolumeJournal(JournalPath); // fresh load from disk
        Assert.Equal(2, second.Count);
        Assert.True(second.TryGet("dev|app1", out float v1));
        Assert.Equal(0.8f, v1, 3);
    }

    [Fact]
    public void Corrupt_journal_file_is_treated_as_empty()
    {
        File.WriteAllText(JournalPath, "{ not valid json !!");
        var journal = new VolumeJournal(JournalPath);
        Assert.Equal(0, journal.Count);
    }

    [Fact]
    public void Clear_removes_entries_and_persists()
    {
        var journal = new VolumeJournal(JournalPath);
        journal.Record("dev|app", 0.5f);
        journal.Clear();

        Assert.Equal(0, new VolumeJournal(JournalPath).Count);
    }

    [Fact]
    public void Out_of_range_volumes_are_rejected_on_load()
    {
        File.WriteAllText(JournalPath,
            """{"Volumes":{"dev|bad":7.5,"dev|good":0.6},"Seen":{}}""");
        var journal = new VolumeJournal(JournalPath);

        Assert.False(journal.TryGet("dev|bad", out _));
        Assert.True(journal.TryGet("dev|good", out float v));
        Assert.Equal(0.6f, v, 3);
    }

    [Fact]
    public void Crash_recovery_restores_attenuated_sessions_and_clears_journal()
    {
        var source = new FakeSource();
        var app = new FakeSession("run1", "video", rawPeak: 0.9f);
        source.Sessions.Add(app);

        // First run: engine journals the original (1.0) and attenuates the app.
        var engine1 = new LevelingEngine(source, new LevelerSettings(), new VolumeJournal(JournalPath));
        for (int i = 0; i < 150; i++)
        {
            engine1.Tick();
        }

        Assert.True(app.Volume < 0.5f);
        // CRASH: engine1 is abandoned without RestoreOriginalVolumes().

        // Second run: the app restarted too — new session instance id, same stable id,
        // and Windows re-applied the attenuated volume it persisted.
        var reborn = new FakeSession("run2", "video", rawPeak: 0.9f)
        {
            StableId = app.StableId,
            Volume = app.Volume,
        };
        source.Sessions.Clear();
        source.Sessions.Add(reborn);

        var engine2 = new LevelingEngine(source, new LevelerSettings(), new VolumeJournal(JournalPath));
        int recovered = engine2.RecoverOrphanedVolumes();

        Assert.Equal(1, recovered);
        Assert.Equal(1.0f, reborn.Volume, 3); // the user's real volume is back
        Assert.Equal(0, new VolumeJournal(JournalPath).Count); // journal consumed
    }

    [Fact]
    public void Recovery_with_no_journal_entries_touches_nothing()
    {
        var source = new FakeSource();
        var app = new FakeSession("a", "video", rawPeak: 0.5f) { Volume = 0.42f };
        source.Sessions.Add(app);

        var engine = new LevelingEngine(source, new LevelerSettings(), new VolumeJournal(JournalPath));
        Assert.Equal(0, engine.RecoverOrphanedVolumes());
        Assert.Equal(0.42f, app.Volume, 3);
    }

    [Fact]
    public void Clean_restore_consumes_journal_entries_for_live_sessions()
    {
        var source = new FakeSource();
        source.Sessions.Add(new FakeSession("a", "video", rawPeak: 0.9f));

        var engine = new LevelingEngine(source, new LevelerSettings(), new VolumeJournal(JournalPath));
        for (int i = 0; i < 50; i++)
        {
            engine.Tick();
        }

        Assert.True(new VolumeJournal(JournalPath).Count > 0);
        engine.RestoreOriginalVolumes();
        Assert.Equal(0, new VolumeJournal(JournalPath).Count);
    }

    [Fact]
    public void App_closed_while_attenuated_keeps_its_entry_and_heals_on_reappearance()
    {
        var source = new FakeSource();
        var app = new FakeSession("run1", "video", rawPeak: 0.9f);
        source.Sessions.Add(app);

        var engine = new LevelingEngine(source, new LevelerSettings(), new VolumeJournal(JournalPath));
        for (int i = 0; i < 150; i++)
        {
            engine.Tick();
        }

        Assert.True(app.Volume < 0.5f);

        // The app is closed mid-leveling: Windows persists the attenuated volume.
        source.Sessions.Clear();
        engine.Tick(); // prunes the dead session
        engine.RestoreOriginalVolumes(); // nothing live to restore — entry must survive
        Assert.Equal(1, new VolumeJournal(JournalPath).Count);

        // The app reopens in a later leveling era with the attenuated volume re-applied.
        var reborn = new FakeSession("run2", "video", rawPeak: 0.9f)
        {
            StableId = app.StableId,
            Volume = app.Volume,
        };
        source.Sessions.Add(reborn);
        for (int i = 0; i < 5; i++)
        {
            engine.Tick(); // first sight adopts the journaled TRUE original
        }

        engine.RestoreOriginalVolumes();
        Assert.Equal(1.0f, reborn.Volume, 3); // healed to the user's real volume
        Assert.Equal(0, new VolumeJournal(JournalPath).Count);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }
}
