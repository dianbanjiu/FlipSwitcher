using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FlipSwitcher.Services;

internal static class Program
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [STAThread]
    private static int Main()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("Blocked A remains deduplicated after B completes", DeduplicatesBlockedWindow),
            ("Capacity is bounded and recovers without queued requests", BoundsConcurrentActivations),
            ("Failed activation releases its handle", ReleasesAfterFailure),
            ("Concurrent requests for one HWND start one activation", DeduplicatesConcurrentRequests),
            ("ViewMode round-trips through JSON with Grid default", ViewMode_RoundTripsThroughJson),
            ("GridDensity density tiers match the spec", GridDensity_TierBoundaries),
            ("GridDensity clamps popup to work area", GridDensity_ClampsToWorkArea),
            ("GridDensity row count covers all windows", GridDensity_RowsCoverAllWindows),
            ("UsageStats counts persist, load, and tolerate corruption", UsageStats_CountsPersistAndLoad),
            ("Usage ordering sorts descending and keeps ties stable", UsageOrder_StableTiesAndDescending)
        };
        int failures = 0;
        foreach (var test in tests)
        {
            try
            {
                test.Run();
                Console.WriteLine($"PASS: {test.Name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL: {test.Name}: {ex}");
            }
        }
        return failures == 0 ? 0 : 1;
    }

    private static void DeduplicatesBlockedWindow()
    {
        var service = new WindowActivationService();
        using var release = new ManualResetEventSlim();
        var a = service.TryActivate((IntPtr)1, () => Assert(release.Wait(Timeout), "A timed out"))!;
        try
        {
            Assert(service.TryActivate((IntPtr)1, () => { }) == null, "Duplicate A accepted");
            Complete(service.TryActivate((IntPtr)2, () =>
            {
                Assert(!Thread.CurrentThread.IsThreadPoolThread, "Activation uses shared pool");
                Assert(Thread.CurrentThread.IsBackground, "Worker prevents process exit");
            })!);
            for (int i = 0; i < 20; i++)
            {
                Assert(service.TryActivate((IntPtr)1, () => { }) == null, "B completion forgot A");
            }
        }
        finally
        {
            release.Set();
            Complete(a);
        }
        Complete(service.TryActivate((IntPtr)1, () => { })!);
    }

    private static void BoundsConcurrentActivations()
    {
        var service = new WindowActivationService();
        using var release = new ManualResetEventSlim();
        var running = new List<Task>();
        int rejectedRuns = 0;
        try
        {
            for (int i = 1; i <= WindowActivationService.MaxConcurrentActivations; i++)
            {
                var task = service.TryActivate((IntPtr)i,
                    () => Assert(release.Wait(Timeout), "Worker timed out"));
                Assert(task != null, "Rejected within capacity");
                running.Add(task!);
            }
            Assert(service.TryActivate((IntPtr)100, () => Interlocked.Increment(ref rejectedRuns)) == null,
                "Exceeded capacity");
        }
        finally
        {
            release.Set();
            Complete(Task.WhenAll(running));
        }
        Assert(rejectedRuns == 0, "Rejected request executed");
        Complete(service.TryActivate((IntPtr)100, () => { })!);
    }

    private static void ReleasesAfterFailure()
    {
        var service = new WindowActivationService();
        var failure = new InvalidOperationException("Simulated activation failure");
        var task = service.TryActivate((IntPtr)1, () => throw failure)!;
        try
        {
            Complete(task);
            throw new Exception("Expected activation failure");
        }
        catch (InvalidOperationException ex) when (ReferenceEquals(ex, failure))
        {
        }
        Complete(service.TryActivate((IntPtr)1, () => { })!);
    }

    private static void DeduplicatesConcurrentRequests()
    {
        var service = new WindowActivationService();
        using var release = new ManualResetEventSlim();
        var requests = new Task?[32];
        try
        {
            Parallel.For(0, requests.Length, i =>
            {
                requests[i] = service.TryActivate((IntPtr)1,
                    () => Assert(release.Wait(Timeout), "Worker timed out"));
            });
            int accepted = 0;
            foreach (var request in requests)
            {
                if (request != null) accepted++;
            }
            Assert(accepted == 1, $"Accepted {accepted} requests for one HWND");
        }
        finally
        {
            release.Set();
            foreach (var request in requests)
            {
                if (request != null) Complete(request);
            }
        }
    }

    private static void Complete(Task task) => task.WaitAsync(Timeout).GetAwaiter().GetResult();

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void AssertEqual<T>(T expected, T actual, string name)
    {
        if (!Equals(expected, actual))
            throw new Exception($"{name}: expected {expected}, got {actual}");
    }

    private static void ViewMode_RoundTripsThroughJson()
    {
        var original = new AppSettings { ViewMode = ViewMode.List };
        string json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<AppSettings>(json)
            ?? throw new Exception("deserialization returned null");

        AssertEqual(ViewMode.List, restored.ViewMode, "List round-trip");

        // Persisted settings without a ViewMode field (pre-upgrade) must default to Grid.
        string legacyJson = "{\"UseAltTab\":true}";
        var legacy = JsonSerializer.Deserialize<AppSettings>(legacyJson)
            ?? throw new Exception("legacy deserialization returned null");
        AssertEqual(ViewMode.Grid, legacy.ViewMode, "legacy defaults to Grid");
        AssertEqual(ViewMode.Grid, new AppSettings().ViewMode, "fresh defaults default to Grid");
    }

    private static void GridDensity_TierBoundaries()
    {
        // Tier 1: 1-3 windows -> columns = count.
        var l1 = GridDensity.Calculate(1, 1920, 1040);
        AssertEqual(1, l1.Columns, "columns@1");
        // 2 is the only tier-1 count that is neither 1 nor 3 (Task 4's LowDensityCardMaxWidth path).
        var l2 = GridDensity.Calculate(2, 1920, 1040);
        AssertEqual(2, l2.Columns, "columns@2");
        var l3 = GridDensity.Calculate(3, 1920, 1040);
        AssertEqual(3, l3.Columns, "columns@3");

        // Tier 2: 4-9 -> 3 columns.
        var l4 = GridDensity.Calculate(4, 1920, 1040);
        AssertEqual(3, l4.Columns, "columns@4");
        var l9 = GridDensity.Calculate(9, 1920, 1040);
        AssertEqual(3, l9.Columns, "columns@9");

        // Tier 3: 10-19 -> 4 columns.
        var l10 = GridDensity.Calculate(10, 1920, 1040);
        AssertEqual(4, l10.Columns, "columns@10");
        var l19 = GridDensity.Calculate(19, 1920, 1040);
        AssertEqual(4, l19.Columns, "columns@19");

        // Tier 4: >=20 -> 5 columns.
        var l20 = GridDensity.Calculate(20, 1920, 1040);
        AssertEqual(5, l20.Columns, "columns@20");
    }

    private static void GridDensity_ClampsToWorkArea()
    {
        // Wide work area: 3 windows -> width = clamp(3*180, 640, 1536) = 640 (lower bound wins).
        var few = GridDensity.Calculate(3, 1920, 1040);
        AssertEqual(640.0, few.PopupWidth, "few windows width");
        // Height: 1 row * 64 + 200 chrome = 264 -> clamped up to 520.
        AssertEqual(520.0, few.PopupHeight, "few windows height");

        // Small work area: 50 windows on 800x600 -> width clamp(5*180=900, 640, 800*0.8=640) = 640,
        // height clamp(10*64+200=840, 520, max(600*0.8=480, 520)=520) = 520.
        var many = GridDensity.Calculate(50, 800, 600);
        AssertEqual(640.0, many.PopupWidth, "small screen width");
        AssertEqual(520.0, many.PopupHeight, "small screen height");

        // Large monitor: 20 windows -> width clamp(900, 640, 1536) = 900.
        var big = GridDensity.Calculate(20, 1920, 1040);
        AssertEqual(900.0, big.PopupWidth, "big screen width");
        // 4 rows * 64 + 200 = 456 -> clamped up to 520.
        AssertEqual(520.0, big.PopupHeight, "big screen height");

        // Zero windows must not throw and falls back to the resting size.
        var empty = GridDensity.Calculate(0, 1920, 1040);
        AssertEqual(640.0, empty.PopupWidth, "empty width");
        AssertEqual(520.0, empty.PopupHeight, "empty height");
    }

    private static void GridDensity_RowsCoverAllWindows()
    {
        // Every count's height must accommodate ceil(count/columns) rows. 25 vs 26 is the
        // smallest discriminating pair: with correct ceil row counts, 25 -> 5 rows (520px,
        // exactly at the minimum) and 26 -> 6 rows (584px), both unclamped-above on
        // 1920x1040; a floor-division row-count regression flattens the pair
        // (floor(26/5)=5 rows -> 520px vs 520px), which fires the assertion.
        // (Pairs like 12/15 or 26/31 remain monotonic under floor division,
        // so they cannot catch it.)
        var l25 = GridDensity.Calculate(25, 1920, 1040);
        var l26 = GridDensity.Calculate(26, 1920, 1040);
        if (l26.PopupHeight <= l25.PopupHeight)
            throw new Exception($"26 windows ({l26.PopupHeight}) should need more height than 25 ({l25.PopupHeight})");
    }

    private static void UsageStats_CountsPersistAndLoad()
    {
        // The internal path-taking constructor keeps tests off the real
        // %APPDATA%\FlipSwitcher\usage.json file.
        var dir = Path.Combine(Path.GetTempPath(), "FlipSwitcherTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "usage.json");
        try
        {
            var service = new UsageStatsService(path);
            service.RecordActivation("alpha");
            service.RecordActivation("alpha");
            service.RecordActivation("beta");

            // Persistence is async + single-flight now; PersistNow drains it synchronously
            // so the re-read below observes the same behavior (2 activations -> count 2).
            service.PersistNow();

            // A fresh instance on the same path must reload the persisted counts.
            var reloaded = new UsageStatsService(path);
            AssertEqual(2, reloaded.GetCount("alpha"), "alpha count persisted");
            AssertEqual(1, reloaded.GetCount("beta"), "beta count persisted");
            AssertEqual(0, reloaded.GetCount("unknown"), "unknown process count is 0");
            AssertEqual(0, reloaded.GetCount(null), "null process count is 0");
            AssertEqual(0, reloaded.GetCount(""), "empty process count is 0");

            // A corrupt file must be tolerated: read as empty, no throw.
            File.WriteAllText(path, "{ this is definitely not valid json");
            var corrupt = new UsageStatsService(path);
            AssertEqual(0, corrupt.GetCount("alpha"), "corrupt file reads as empty");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static void UsageOrder_StableTiesAndDescending()
    {
        static int CountOf(string name) => name switch
        {
            "a" => 5,
            "b" => 9,
            "c" => 5,
            _ => 0
        };

        // Higher count first; equal counts keep the input (Z-)order: [a,b,c] -> [b,a,c].
        var ordered = UsageOrdering.Apply(new[] { "a", "b", "c" }, x => x, CountOf);
        AssertEqual("b", ordered[0], "highest count first");
        AssertEqual("a", ordered[1], "tie keeps original order (a before c)");
        AssertEqual("c", ordered[2], "tie keeps original order (a before c)");

        // All-zero counts: nothing to order, input order must be preserved exactly.
        var zeros = UsageOrdering.Apply(new[] { "x", "y", "z" }, x => x, _ => 0);
        AssertEqual("x", zeros[0], "all-zero: unchanged 1st");
        AssertEqual("y", zeros[1], "all-zero: unchanged 2nd");
        AssertEqual("z", zeros[2], "all-zero: unchanged 3rd");

        // Empty in, empty out.
        AssertEqual(0, UsageOrdering.Apply(Array.Empty<string>(), x => x, _ => 0).Count, "empty list");
    }
}
