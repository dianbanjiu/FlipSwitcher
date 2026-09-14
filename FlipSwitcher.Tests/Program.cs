using System;
using System.Collections.Generic;
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
            ("Concurrent requests for one HWND start one activation", DeduplicatesConcurrentRequests)
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
}
