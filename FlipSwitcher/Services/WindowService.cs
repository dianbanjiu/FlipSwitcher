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

    private static bool ShouldSkipWindow(IntPtr hWnd, IntPtr shellWindow, uint currentProcessId)
    {
        if (hWnd == shellWindow || !NativeMethods.IsWindowVisible(hWnd))
            return true;

        if (IsCloaked(hWnd))
            return true;

        var exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);
        if ((exStyle & (int)NativeMethods.WS_EX_TOOLWINDOW) != 0 &&
            (exStyle & (int)NativeMethods.WS_EX_APPWINDOW) == 0)
            return true;

        NativeMethods.GetWindowThreadProcessId(hWnd, out uint processId);
        if (processId == currentProcessId)
            return true;

        var owner = NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER);
        if (owner != IntPtr.Zero)
        {
            var rootOwner = NativeMethods.GetAncestor(hWnd, NativeMethods.GA_ROOTOWNER);
            var lastPopup = NativeMethods.GetLastActivePopup(rootOwner);
            if (lastPopup != hWnd && NativeMethods.IsWindowVisible(lastPopup))
                return true;
        }

        var titleLength = NativeMethods.GetWindowTextLength(hWnd);
        if (titleLength == 0)
            return true;

        var titleBuilder = new StringBuilder(titleLength + 1);
        NativeMethods.GetWindowText(hWnd, titleBuilder, titleBuilder.Capacity);
        if (string.IsNullOrWhiteSpace(titleBuilder.ToString()))
            return true;

        var classBuilder = new StringBuilder(MaxClassNameLength);
        NativeMethods.GetClassName(hWnd, classBuilder, classBuilder.Capacity);
        if (ExcludedClassNames.Contains(classBuilder.ToString()))
            return true;

        var processName = GetProcessName(processId);
        if (ExcludedProcessNames.Contains(processName))
            return true;

        return false;
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

        NativeMethods.EnumWindows((hWnd, lParam) =>
        {
            try
            {
                if (ShouldSkipWindow(hWnd, shellWindow, currentProcessId))
                    return true;

                var titleLength = NativeMethods.GetWindowTextLength(hWnd);
                var titleBuilder = new StringBuilder(titleLength + 1);
                NativeMethods.GetWindowText(hWnd, titleBuilder, titleBuilder.Capacity);
                var title = titleBuilder.ToString();

                var classBuilder = new StringBuilder(MaxClassNameLength);
                NativeMethods.GetClassName(hWnd, classBuilder, classBuilder.Capacity);
                var className = classBuilder.ToString();

                NativeMethods.GetWindowThreadProcessId(hWnd, out uint processId);
                var processName = GetProcessName(processId);
                var (isMinimized, isMaximized) = GetWindowState(hWnd);

                var window = new AppWindow(hWnd, title, className, processId, processName, isMinimized, isMaximized);
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

