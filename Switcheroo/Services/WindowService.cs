using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Switcheroo.Core;
using Switcheroo.Models;

namespace Switcheroo.Services;

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

    public List<AppWindow> GetWindows()
    {
        var windows = new List<AppWindow>();
        var shellWindow = NativeMethods.GetShellWindow();
        var currentProcessId = (uint)Environment.ProcessId;

        NativeMethods.EnumWindows((hWnd, lParam) =>
        {
            try
            {
                // Skip shell window
                if (hWnd == shellWindow)
                    return true;

                // Must be visible
                if (!NativeMethods.IsWindowVisible(hWnd))
                    return true;

                // Check if cloaked (hidden by Windows)
                if (IsCloaked(hWnd))
                    return true;

                // Get window style
                var exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);

                // Skip tool windows unless they have app window style
                if ((exStyle & (int)NativeMethods.WS_EX_TOOLWINDOW) != 0 &&
                    (exStyle & (int)NativeMethods.WS_EX_APPWINDOW) == 0)
                    return true;

                // Get process info first to skip our own process
                NativeMethods.GetWindowThreadProcessId(hWnd, out uint processId);
                
                // Skip our own windows
                if (processId == currentProcessId)
                    return true;

                // Check ownership - we want top-level windows
                var owner = NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER);
                if (owner != IntPtr.Zero)
                {
                    // Has an owner, check if it's the last active popup of its root
                    var rootOwner = NativeMethods.GetAncestor(hWnd, NativeMethods.GA_ROOTOWNER);
                    var lastPopup = NativeMethods.GetLastActivePopup(rootOwner);
                    if (lastPopup != hWnd && NativeMethods.IsWindowVisible(lastPopup))
                        return true;
                }

                // Get window title
                var titleLength = NativeMethods.GetWindowTextLength(hWnd);
                if (titleLength == 0)
                    return true;

                var titleBuilder = new StringBuilder(titleLength + 1);
                NativeMethods.GetWindowText(hWnd, titleBuilder, titleBuilder.Capacity);
                var title = titleBuilder.ToString();

                if (string.IsNullOrWhiteSpace(title))
                    return true;

                // Get class name
                var classBuilder = new StringBuilder(256);
                NativeMethods.GetClassName(hWnd, classBuilder, classBuilder.Capacity);
                var className = classBuilder.ToString();

                // Skip excluded class names
                if (ExcludedClassNames.Contains(className))
                    return true;

                var processName = GetProcessName(processId);

                // Skip excluded processes
                if (ExcludedProcessNames.Contains(processName))
                    return true;

                // Check window state
                var isMinimized = NativeMethods.IsIconic(hWnd);
                var isMaximized = NativeMethods.IsZoomed(hWnd);
                
                // For minimized windows, check if they were maximized before minimizing
                if (isMinimized)
                {
                    var placement = new NativeMethods.WINDOWPLACEMENT();
                    placement.length = Marshal.SizeOf(placement);
                    if (NativeMethods.GetWindowPlacement(hWnd, ref placement))
                    {
                        // Check if the window would be maximized when restored
                        isMaximized = (placement.flags & 0x2) != 0; // WPF_RESTORETOMAXIMIZED
                    }
                }

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

