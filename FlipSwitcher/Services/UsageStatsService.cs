using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FlipSwitcher.Services;

/// <summary>
/// Persists per-process-name activation counts used for usage-frequency ordering
/// ("常用的排在前头"). Counts live in <c>%APPDATA%\FlipSwitcher\usage.json</c>,
/// deliberately separate from <c>settings.json</c> so stats churn never rewrites user
/// settings (and a corrupt stats file can never take settings down with it).
/// </summary>
/// <remarks>
/// Persistence is asynchronous and single-flight: <see cref="RecordActivation"/> updates
/// the in-memory dictionary synchronously (so <see cref="GetCount"/> is always coherent
/// and never touches disk) and then schedules one background write. Writes that arrive
/// while a persist is running just set a dirty flag that the running/follow-up pass
/// drains — activations never queue unbounded tasks and never block the UI thread on IO.
/// <see cref="PersistNow"/> offers a synchronous drain for tests and shutdown paths.
/// </remarks>
public class UsageStatsService
{
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FlipSwitcher",
        "usage.json");

    private static UsageStatsService? _instance;

    /// <summary>Lazy singleton, same shape as <see cref="SettingsService.Instance"/>.</summary>
    public static UsageStatsService Instance => _instance ??= new UsageStatsService();

    private readonly string _filePath;

    // Guards the dictionary only — held briefly, never during IO.
    private readonly object _gate = new();
    private Dictionary<string, int> _counts;

    // Persist machinery. _persistRunning means a drain pass owns the write baton;
    // _dirty marks counts changed since the pass's last snapshot. Both live under
    // _persistGate so baton release and the final dirty check are atomic.
    private readonly object _persistGate = new();
    private bool _persistRunning;
    private bool _dirty;

    /// <summary>Uses the default <c>%APPDATA%\FlipSwitcher\usage.json</c> location.</summary>
    public UsageStatsService() : this(DefaultPath)
    {
    }

    /// <summary>
    /// Internal constructor with an explicit file path. Exists so tests can exercise
    /// persistence against a temp file without touching the real user profile.
    /// </summary>
    internal UsageStatsService(string filePath)
    {
        _filePath = filePath;
        _counts = Load();
    }

    /// <summary>
    /// Activation count for a process name; 0 for unknown, null, or empty names.
    /// </summary>
    public int GetCount(string? processName)
    {
        if (string.IsNullOrEmpty(processName)) return 0;
        lock (_gate)
        {
            return _counts.TryGetValue(processName, out var count) ? count : 0;
        }
    }

    /// <summary>
    /// Increment the activation count for a process name and schedule a persist. The
    /// dictionary update is synchronous (cheap, keeps <see cref="GetCount"/> coherent);
    /// the disk write runs on the thread pool via a single-flight drain so the switch
    /// path never blocks on IO. Null/empty names are ignored. IO errors are swallowed:
    /// a stats write failure must never break switching.
    /// </summary>
    public void RecordActivation(string? processName)
    {
        if (string.IsNullOrEmpty(processName)) return;
        lock (_gate)
        {
            _counts[processName] = (_counts.TryGetValue(processName, out var count) ? count : 0) + 1;
        }
        SchedulePersist();
    }

    /// <summary>
    /// Synchronously flush any pending count changes to disk. Single-flight-compatible:
    /// waits for an in-flight background persist to release the baton, then drains.
    /// Intended for tests (which need the async persist to be observable before
    /// re-instantiating against the same file) and future shutdown paths.
    /// </summary>
    internal void PersistNow()
    {
        while (true)
        {
            lock (_persistGate)
            {
                if (_persistRunning)
                {
                    Monitor.Wait(_persistGate); // the drain pulses when it releases the baton
                    continue;
                }
                _persistRunning = true;
            }
            PersistDrain();
            return;
        }
    }

    /// <summary>
    /// Kick off one background drain pass. If a pass is already running, just mark the
    /// counts dirty — the running pass (or its follow-up) picks the change up; no task
    /// queuing.
    /// </summary>
    private void SchedulePersist()
    {
        lock (_persistGate)
        {
            if (_persistRunning)
            {
                _dirty = true;
                return;
            }
            _persistRunning = true;
        }
        _ = Task.Run(PersistDrain);
    }

    /// <summary>
    /// Own the write baton until the counts are clean. Exit and baton release are
    /// checked/issued atomically under <see cref="_persistGate"/> so a change landing
    /// mid-drain can never be orphaned with the baton free.
    /// </summary>
    private void PersistDrain()
    {
        try
        {
            while (true)
            {
                lock (_persistGate)
                {
                    if (!_dirty)
                    {
                        _persistRunning = false;
                        Monitor.PulseAll(_persistGate);
                        return;
                    }
                    _dirty = false;
                }
                Save(SnapshotCounts());
            }
        }
        catch
        {
            // Save swallows IO errors, so this is defensive only: never leak the baton.
            lock (_persistGate)
            {
                _persistRunning = false;
                Monitor.PulseAll(_persistGate);
            }
        }
    }

    private Dictionary<string, int> SnapshotCounts()
    {
        lock (_gate)
        {
            return new Dictionary<string, int>(_counts);
        }
    }

    /// <summary>
    /// Load counts from disk. A missing or corrupt file yields an empty dictionary —
    /// stats are expendable and must never prevent startup.
    /// </summary>
    private Dictionary<string, int> Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<Dictionary<string, int>>(json)
                       ?? new Dictionary<string, int>();
            }
        }
        catch
        {
            // Missing/corrupt file -> start empty.
        }
        return new Dictionary<string, int>();
    }

    /// <summary>
    /// Persist a snapshot of the counts with an atomic temp-file + replace write (same
    /// pattern as <see cref="SettingsService.Save"/>) so a crash mid-write cannot truncate
    /// the file. Takes a pre-copied snapshot because it runs off the UI thread while the
    /// live dictionary keeps mutating under <see cref="_gate"/>.
    /// </summary>
    private void Save(Dictionary<string, int> counts)
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(counts, new JsonSerializerOptions { WriteIndented = true });

            var tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch
        {
            // Ignore save errors (locked file, disk full, ...) — stats are best-effort.
        }
    }
}
