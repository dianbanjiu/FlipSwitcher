using System;
using System.Diagnostics;
using FlipSwitcher.Core;
using FlipSwitcher.Models;

namespace FlipSwitcher.Services;

public enum WindowActivationOutcome
{
    Requested,
    NoSelection,
    TargetClosed,
    ForegroundDenied,
    NativeFailure
}

public readonly record struct WindowActivationResult(
    WindowActivationOutcome Outcome,
    bool RestoreRequested = false,
    bool RestoreQueued = false)
{
    public bool ShouldHideSwitcher => Outcome == WindowActivationOutcome.Requested;
}

internal interface IWindowActivationService
{
    WindowActivationResult TryActivate(AppWindow window);
}

internal interface IWindowActivationNativeApi
{
    bool IsWindow(IntPtr handle);
    bool IsWindowVisible(IntPtr handle);
    bool IsIconic(IntPtr handle);
    IntPtr GetLastActivePopup(IntPtr handle);
    bool ShowWindowAsync(IntPtr handle, int command);
    bool SetForegroundWindow(IntPtr handle);
}

internal sealed class WindowActivationService : IWindowActivationService
{
    private readonly IWindowActivationNativeApi _native;

    public WindowActivationService()
        : this(new WindowActivationNativeApi())
    {
    }

    internal WindowActivationService(IWindowActivationNativeApi native)
    {
        _native = native;
    }

    public WindowActivationResult TryActivate(AppWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var root = window.Handle;
            if (root == IntPtr.Zero || !_native.IsWindow(root))
                return new WindowActivationResult(WindowActivationOutcome.TargetClosed);

            var target = ResolveActivationTarget(root);

            bool restoreRequested = _native.IsIconic(root);
            bool restoreQueued = false;
            if (restoreRequested)
            {
                // This posts to the target queue and returns without waiting for its UI thread.
                restoreQueued = _native.ShowWindowAsync(root, NativeMethods.SW_RESTORE);
            }

            if (_native.SetForegroundWindow(target))
            {
                return new WindowActivationResult(
                    WindowActivationOutcome.Requested,
                    restoreRequested,
                    restoreQueued);
            }

            // An owned popup can disappear between resolution and activation. Retry the root
            // only for that race; a live popup must remain the focus target for modal windows.
            if (target != root && !_native.IsWindow(target) && _native.IsWindow(root) &&
                _native.SetForegroundWindow(root))
            {
                return new WindowActivationResult(
                    WindowActivationOutcome.Requested,
                    restoreRequested,
                    restoreQueued);
            }

            return new WindowActivationResult(
                _native.IsWindow(root)
                    ? WindowActivationOutcome.ForegroundDenied
                    : WindowActivationOutcome.TargetClosed,
                restoreRequested,
                restoreQueued);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Window activation failed: {ex}");
            return new WindowActivationResult(WindowActivationOutcome.NativeFailure);
        }
        finally
        {
            stopwatch.Stop();
            if (stopwatch.ElapsedMilliseconds >= 100)
            {
                Debug.WriteLine(
                    $"Window activation request took {stopwatch.ElapsedMilliseconds} ms.");
            }
        }
    }

    private IntPtr ResolveActivationTarget(IntPtr root)
    {
        var popup = _native.GetLastActivePopup(root);
        if (popup != IntPtr.Zero &&
            popup != root &&
            _native.IsWindow(popup) &&
            _native.IsWindowVisible(popup))
        {
            return popup;
        }

        return root;
    }
}

internal sealed class WindowActivationNativeApi : IWindowActivationNativeApi
{
    public bool IsWindow(IntPtr handle) => NativeMethods.IsWindow(handle);
    public bool IsWindowVisible(IntPtr handle) => NativeMethods.IsWindowVisible(handle);
    public bool IsIconic(IntPtr handle) => NativeMethods.IsIconic(handle);
    public IntPtr GetLastActivePopup(IntPtr handle) => NativeMethods.GetLastActivePopup(handle);
    public bool ShowWindowAsync(IntPtr handle, int command) =>
        NativeMethods.ShowWindowAsync(handle, command);
    public bool SetForegroundWindow(IntPtr handle) =>
        NativeMethods.SetForegroundWindow(handle);
}
