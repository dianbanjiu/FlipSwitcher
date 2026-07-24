using FlipSwitcher.Models;
using FlipSwitcher.Services;
using FlipSwitcher.Core;
using System.Windows;

namespace FlipSwitcher.Tests;

internal static class Program
{
    private static readonly IntPtr Root = new(101);
    private static readonly IntPtr Popup = new(202);

    [STAThread]
    private static int Main()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("Normal window requests foreground without restore", NormalWindow),
            ("Minimized window queues asynchronous restore first", MinimizedWindow),
            ("Visible owned popup receives foreground", VisiblePopup),
            ("Closed root is rejected without activation", ClosedRoot),
            ("Disappearing popup retries root once", DisappearingPopup),
            ("Live popup denial does not bypass modal target", LivePopupDenied),
            ("Native failure becomes a structured result", NativeFailure),
            ("Only accepted requests hide the switcher", HideSwitcherSemantics),
            ("Keyboard hook uses a dedicated message thread", KeyboardHookLifecycle),
            ("Hung target cannot block activation caller", HungTargetDoesNotBlock),
            ("Minimized hung target restore is asynchronous", MinimizedHungTargetDoesNotBlock)
        };

        int failed = 0;
        foreach (var test in tests)
        {
            try
            {
                test.Run();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"{tests.Length - failed}/{tests.Length} regression tests passed.");
        return failed == 0 ? 0 : 1;
    }

    private static void NormalWindow()
    {
        var native = CreateNative();
        var result = Activate(native);

        Equal(WindowActivationOutcome.Requested, result.Outcome);
        False(result.RestoreRequested);
        SequenceEqual(["Foreground:101"], native.Calls);
    }

    private static void MinimizedWindow()
    {
        var native = CreateNative();
        native.Iconic = true;

        var result = Activate(native);

        Equal(WindowActivationOutcome.Requested, result.Outcome);
        True(result.RestoreRequested);
        True(result.RestoreQueued);
        SequenceEqual(["Restore:101:9", "Foreground:101"], native.Calls);
    }

    private static void VisiblePopup()
    {
        var native = CreateNative();
        native.Popup = Popup;
        native.Visible.Add(Popup);
        native.Windows.Add(Popup);

        var result = Activate(native);

        Equal(WindowActivationOutcome.Requested, result.Outcome);
        SequenceEqual(["Foreground:202"], native.Calls);
    }

    private static void ClosedRoot()
    {
        var native = new FakeNativeApi();

        var result = Activate(native);

        Equal(WindowActivationOutcome.TargetClosed, result.Outcome);
        Equal(0, native.Calls.Count);
    }

    private static void DisappearingPopup()
    {
        var native = CreateNative();
        native.Popup = Popup;
        native.Visible.Add(Popup);
        native.Windows.Add(Popup);
        native.ForegroundResults.Enqueue(false);
        native.ForegroundResults.Enqueue(true);
        native.IsWindowOverride = handle =>
        {
            if (handle != Popup)
                return native.Windows.Contains(handle);

            native.PopupChecks++;
            return native.PopupChecks == 1;
        };

        var result = Activate(native);

        Equal(WindowActivationOutcome.Requested, result.Outcome);
        SequenceEqual(["Foreground:202", "Foreground:101"], native.Calls);
    }

    private static void LivePopupDenied()
    {
        var native = CreateNative();
        native.Popup = Popup;
        native.Visible.Add(Popup);
        native.Windows.Add(Popup);
        native.ForegroundResults.Enqueue(false);

        var result = Activate(native);

        Equal(WindowActivationOutcome.ForegroundDenied, result.Outcome);
        SequenceEqual(["Foreground:202"], native.Calls);
    }

    private static void NativeFailure()
    {
        var native = CreateNative();
        native.ThrowOnForeground = true;

        var result = Activate(native);

        Equal(WindowActivationOutcome.NativeFailure, result.Outcome);
    }

    private static void HideSwitcherSemantics()
    {
        True(new WindowActivationResult(WindowActivationOutcome.Requested).ShouldHideSwitcher);
        False(new WindowActivationResult(WindowActivationOutcome.TargetClosed).ShouldHideSwitcher);
        False(new WindowActivationResult(WindowActivationOutcome.ForegroundDenied).ShouldHideSwitcher);
        False(new WindowActivationResult(WindowActivationOutcome.NativeFailure).ShouldHideSwitcher);
    }

    private static void KeyboardHookLifecycle()
    {
        var window = new Window
        {
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };
        var service = new HotkeyService();

        try
        {
            service.RegisterHotkeys(window, useAltSpace: false, useAltTab: true);

            True(service.IsKeyboardHookRunning);
            True(service.KeyboardHookThreadId != 0);
            True(service.KeyboardHookThreadId != NativeMethods.GetCurrentThreadId());

            service.RegisterHotkeys(window, useAltSpace: false, useAltTab: true);
            True(service.IsKeyboardHookRunning);

            service.UnregisterAllHotkeys();
            False(service.IsKeyboardHookRunning);
        }
        finally
        {
            service.Dispose();
        }
    }

    private static void HungTargetDoesNotBlock()
    {
        RunHungWindowTest(minimized: false);
    }

    private static void MinimizedHungTargetDoesNotBlock()
    {
        var result = RunHungWindowTest(minimized: true);
        True(result.RestoreRequested);
    }

    private static WindowActivationResult RunHungWindowTest(bool minimized)
    {
        using var ready = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        IntPtr handle = IntPtr.Zero;
        Exception? windowThreadFailure = null;

        var windowThread = new Thread(() =>
        {
            try
            {
                var window = new Window
                {
                    Width = 1,
                    Height = 1,
                    Left = -32000,
                    Top = -32000,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.ToolWindow,
                    WindowState = minimized ? WindowState.Minimized : WindowState.Normal,
                    Title = "FlipSwitcher hung-window regression target"
                };

                window.Show();
                handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                ready.Set();

                // Deliberately stop servicing this HWND's message queue until the test releases it.
                release.Wait();
                window.Close();
            }
            catch (Exception ex)
            {
                windowThreadFailure = ex;
                ready.Set();
            }
        })
        {
            IsBackground = true,
            Name = "FlipSwitcher hung-window test"
        };
        windowThread.SetApartmentState(ApartmentState.STA);
        windowThread.Start();
        using var watchdog = new Timer(
            _ => release.Set(),
            null,
            TimeSpan.FromSeconds(5),
            Timeout.InfiniteTimeSpan);

        try
        {
            True(ready.Wait(TimeSpan.FromSeconds(5)));
            if (windowThreadFailure != null)
                throw new InvalidOperationException("Hung-window setup failed.", windowThreadFailure);
            True(handle != IntPtr.Zero);

            var target = new AppWindow(
                handle,
                "Hung test window",
                "HwndWrapper",
                43,
                "FlipSwitcher.Tests",
                minimized,
                isMaximized: false);
            var service = new WindowActivationService();

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = service.TryActivate(target);
            stopwatch.Stop();

            if (stopwatch.Elapsed > TimeSpan.FromMilliseconds(500))
            {
                throw new InvalidOperationException(
                    $"Activation waited {stopwatch.ElapsedMilliseconds} ms for a hung target.");
            }

            return result;
        }
        finally
        {
            release.Set();
            True(windowThread.Join(TimeSpan.FromSeconds(5)));
        }
    }

    private static WindowActivationResult Activate(FakeNativeApi native)
    {
        var service = new WindowActivationService(native);
        var window = new AppWindow(
            Root,
            "Test window",
            "TestWindowClass",
            42,
            "test",
            isMinimized: native.Iconic,
            isMaximized: false);
        return service.TryActivate(window);
    }

    private static FakeNativeApi CreateNative()
    {
        var native = new FakeNativeApi();
        native.Windows.Add(Root);
        return native;
    }

    private static void True(bool value)
    {
        if (!value)
            throw new InvalidOperationException("Expected true.");
    }

    private static void False(bool value)
    {
        if (value)
            throw new InvalidOperationException("Expected false.");
    }

    private static void Equal<T>(T expected, T actual)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    private static void SequenceEqual(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
        }
    }

    private sealed class FakeNativeApi : IWindowActivationNativeApi
    {
        public HashSet<IntPtr> Windows { get; } = [];
        public HashSet<IntPtr> Visible { get; } = [];
        public List<string> Calls { get; } = [];
        public Queue<bool> ForegroundResults { get; } = [];
        public IntPtr Popup { get; set; }
        public bool Iconic { get; set; }
        public bool RestoreResult { get; set; } = true;
        public bool ThrowOnForeground { get; set; }
        public int PopupChecks { get; set; }
        public Func<IntPtr, bool>? IsWindowOverride { get; set; }

        public bool IsWindow(IntPtr handle) =>
            IsWindowOverride?.Invoke(handle) ?? Windows.Contains(handle);

        public bool IsWindowVisible(IntPtr handle) => Visible.Contains(handle);

        public bool IsIconic(IntPtr handle) => Iconic;

        public IntPtr GetLastActivePopup(IntPtr handle) =>
            Popup == IntPtr.Zero ? handle : Popup;

        public bool ShowWindowAsync(IntPtr handle, int command)
        {
            Calls.Add($"Restore:{handle.ToInt64()}:{command}");
            return RestoreResult;
        }

        public bool SetForegroundWindow(IntPtr handle)
        {
            Calls.Add($"Foreground:{handle.ToInt64()}");
            if (ThrowOnForeground)
                throw new InvalidOperationException("Simulated native failure.");
            return ForegroundResults.Count == 0 || ForegroundResults.Dequeue();
        }
    }
}
