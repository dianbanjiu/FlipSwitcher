using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FlipSwitcher.Core;
using FlipSwitcher.Models;

namespace FlipSwitcher.Services;

/// <summary>
/// Service for enumerating and managing windows
/// </summary>
public class WindowService
{
    private static readonly HashSet<string> ExcludedClassNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "Button",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "DV2ControlHost",
        "MssgrIMWindow",
        "SysShadow",
        "Xaml_WindowedPopupClass",
        "Windows.UI.Core.CoreWindow" // Core window inside ApplicationFrameWindow
    };

    private static readonly HashSet<string> ExcludedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SearchHost",
        "ShellExperienceHost",
        "StartMenuExperienceHost",
        "SearchUI",
        "LockApp",
        "TextInputHost"
    };

    private const int WPF_RESTORETOMAXIMIZED = 0x2;
    private const int MaxClassNameLength = 256;

    private record struct WindowInfo(string Title, string ClassName, uint ProcessId, string ProcessName);

    private static WindowInfo? TryGetWindowInfo(IntPtr hWnd, IntPtr shellWindow, uint currentProcessId)
    {
        if (hWnd == shellWindow || !NativeMethods.IsWindowVisible(hWnd))
            return null;

        if (IsCloaked(hWnd))
            return null;

        var exStyle = (long)NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE);
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0 &&
            (exStyle & NativeMethods.WS_EX_APPWINDOW) == 0)
            return null;

        // Filter windows with WS_EX_NOACTIVATE (non-activatable windows should not appear in task switcher)
        if ((exStyle & NativeMethods.WS_EX_NOACTIVATE) != 0 &&
            (exStyle & NativeMethods.WS_EX_APPWINDOW) == 0)
            return null;

        // Filter windows that are too small (skip check for minimized windows)
        if (!NativeMethods.IsIconic(hWnd) && !HasValidWindowSize(hWnd))
            return null;

        NativeMethods.GetWindowThreadProcessId(hWnd, out uint processId);
        if (processId == currentProcessId)
            return null;

        var owner = NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER);
        if (owner != IntPtr.Zero && (exStyle & NativeMethods.WS_EX_APPWINDOW) == 0)
        {
            // Walk the owner chain: if any owner in the chain is visible, this window is a
            // subordinate dialog (e.g. Environment Variables owned by System Properties) and
            // should be hidden. If all owners are invisible (e.g. a hidden launcher window),
            // the window itself is the "top" of the visible chain and should be shown.
            var current = owner;
            while (current != IntPtr.Zero)
            {
                if (NativeMethods.IsWindowVisible(current))
                    return null;
                current = NativeMethods.GetWindow(current, NativeMethods.GW_OWNER);
            }
        }

        var titleLength = NativeMethods.GetWindowTextLength(hWnd);
        if (titleLength == 0)
            return null;

        var titleBuilder = new StringBuilder(titleLength + 1);
        NativeMethods.GetWindowText(hWnd, titleBuilder, titleBuilder.Capacity);
        var title = titleBuilder.ToString();
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var classBuilder = new StringBuilder(MaxClassNameLength);
        NativeMethods.GetClassName(hWnd, classBuilder, classBuilder.Capacity);
        var className = classBuilder.ToString();
        if (ExcludedClassNames.Contains(className))
            return null;

        var processName = GetProcessName(processId);
        if (ExcludedProcessNames.Contains(processName))
            return null;

        return new WindowInfo(title, className, processId, processName);
    }

    private static (bool isMinimized, bool isMaximized) GetWindowState(IntPtr hWnd)
    {
        var isMinimized = NativeMethods.IsIconic(hWnd);
        var isMaximized = NativeMethods.IsZoomed(hWnd);

        if (isMinimized)
        {
            var placement = new NativeMethods.WINDOWPLACEMENT();
            placement.length = Marshal.SizeOf(placement);
            if (NativeMethods.GetWindowPlacement(hWnd, ref placement))
            {
                isMaximized = (placement.flags & WPF_RESTORETOMAXIMIZED) != 0;
            }
        }

        return (isMinimized, isMaximized);
    }

    public List<AppWindow> GetWindows()
    {
        var windows = new List<AppWindow>();
        var shellWindow = NativeMethods.GetShellWindow();
        var currentProcessId = (uint)Environment.ProcessId;

        // Enumerate monitors once and share across all AppWindow instances
        var monitors = new List<IntPtr>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMon, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data) =>
            {
                monitors.Add(hMon);
                return true;
            }, IntPtr.Zero);

        NativeMethods.EnumWindows((hWnd, lParam) =>
        {
            try
            {
                var info = TryGetWindowInfo(hWnd, shellWindow, currentProcessId);
                if (info is null)
                    return true;

                var (isMinimized, isMaximized) = GetWindowState(hWnd);
                var window = new AppWindow(hWnd, info.Value.Title, info.Value.ClassName,
                    info.Value.ProcessId, info.Value.ProcessName, isMinimized, isMaximized, monitors);
                windows.Add(window);
            }
            catch
            {
                // Skip windows that cause errors
            }

            return true;
        }, IntPtr.Zero);

        return windows;
    }

    private static bool IsCloaked(IntPtr hWnd)
    {
        try
        {
            var result = NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED,
                out int cloakedState, sizeof(int));
            return result == 0 && cloakedState != 0;
        }
        catch
        {
            return false;
        }
    }

    private const int MinWindowSize = 50; // Minimum window size threshold

    private static bool HasValidWindowSize(IntPtr hWnd)
    {
        try
        {
            if (NativeMethods.GetWindowRect(hWnd, out var rect))
            {
                var width = rect.Right - rect.Left;
                var height = rect.Bottom - rect.Top;
                // Filter windows that are too small (e.g., 2x2 hidden windows)
                return width >= MinWindowSize && height >= MinWindowSize;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string GetProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return "Unknown";
        }
    }
}

