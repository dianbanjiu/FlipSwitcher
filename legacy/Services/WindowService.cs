using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using FlipSwitcher.Core;
using FlipSwitcher.Models;

namespace FlipSwitcher.Services;

/// <summary>
/// Service for enumerating and managing windows.
/// </summary>
/// <remarks>
/// Performance notes (rev 2):
/// <list type="bullet">
///   <item>Process-name and elevation caches live for the lifetime of the service so
///         repeatedly opening the switcher doesn't re-query the same processes.</item>
///   <item>Filters are ordered cheap-first: visibility, cloak and style flags short-circuit
///         before any string allocation or process query.</item>
///   <item>Per-call delegates for <c>EnumWindows</c> / <c>EnumDisplayMonitors</c> are now
///         instance fields, so we don't allocate a new closure on every refresh.</item>
///   <item>Existing <see cref="AppWindow"/> instances are reused across refreshes when
///         the underlying HWND is unchanged. This preserves their async-loaded icon and
///         their elevation cache.</item>
/// </list>
/// Ordering notes (rev 3):
/// <list type="bullet">
///   <item><c>EnumWindows</c> walks Z-order top-to-bottom, and the OS pins every window
///         carrying <c>WS_EX_TOPMOST</c> ahead of all non-topmost windows. Without
///         intervention that means a Picture-in-Picture popup, an always-on-top utility,
///         or a notification balloon would permanently occupy the head of the switcher,
///         hiding the genuine MRU window. We split topmost windows into a separate bucket
///         and append them after the normal Z-ordered ones in <see cref="GetWindows"/>.</item>
/// </list>
/// </remarks>
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
        "Windows.UI.Core.CoreWindow"
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

    // Locally scoped — only used by the Delphi-VCL compatibility path below. Not promoted to
    // NativeMethods because the rest of the codebase has no reason to know about it.
    private const long WS_EX_DLGMODALFRAME = 0x00000001L;

    private record struct WindowInfo(string Title, string ClassName, uint ProcessId, string ProcessName, bool IsTopmost, IntPtr OwnerHandle);

    // Long-lived caches reused across GetWindows() calls.
    private readonly Dictionary<uint, string> _processNameCache = new();
    private readonly Dictionary<uint, bool> _elevationCache = new();
    private readonly Dictionary<IntPtr, AppWindow> _windowInstanceCache = new();

    // Reused buffers / collections — avoids per-call allocation in the hot path.
    private readonly StringBuilder _titleBuilder = new(256);
    private readonly StringBuilder _classBuilder = new(MaxClassNameLength);
    private readonly List<IntPtr> _monitors = new();
    private readonly List<NativeMethods.RECT> _monitorRects = new();
    private readonly List<AppWindow> _windowsScratch = new();
    // Topmost windows (e.g. browser Picture-in-Picture) are bubbled to the top of Z-order
    // by the OS regardless of MRU. We collect them separately so they don't displace
    // genuinely most-recently-used windows from the head of the switcher list. They are
    // appended after the normal Z-ordered windows in GetWindows().
    private readonly List<AppWindow> _topmostScratch = new();
    private readonly HashSet<IntPtr> _seenHandles = new();
    private readonly HashSet<uint> _seenProcessIds = new();
    // Maps an owned dialog HWND to the visible owner HWND that caused it to be kept via the
    // Delphi-VCL compatibility path (IsLikelyUserOwnedDialog). After enumeration, GetWindows()
    // drops any such dialog whose owner is ALSO present in the switcher list — e.g. the
    // "Environment Variables" dialog owned by the visible "System Properties" window. For
    // Delphi VCL apps the visible owner is a hidden TApplication proxy that never appears in the
    // list, so the owned dialog is retained.
    private readonly Dictionary<IntPtr, IntPtr> _ownedDialogOwners = new();

    // Stable delegate instances — Win32 callbacks pin these via the lifetime of the service.
    private readonly NativeMethods.MonitorEnumProc _monitorEnumProc;
    private readonly NativeMethods.EnumWindowsProc _enumWindowsProc;

    // Per-enumeration state passed to the Win32 callbacks. Set inside GetWindows() before EnumWindows().
    private IntPtr _shellWindow;
    private uint _currentProcessId;

    public WindowService()
    {
        _monitorEnumProc = MonitorEnumCallback;
        _enumWindowsProc = EnumWindowsCallback;
    }

    /// <summary>
    /// Returns true when a layered window is invisible to the user. See AppWindow rev1 for the
    /// full case discussion (transparent tray-only helper anchors, alpha=0, color-key only).
    /// </summary>
    private static bool IsFullyTransparentLayered(IntPtr hWnd, long exStyle)
    {
        if ((exStyle & NativeMethods.WS_EX_LAYERED) == 0)
            return false;

        if (!NativeMethods.GetLayeredWindowAttributes(hWnd, out _, out byte alpha, out uint flags))
            return false;

        if (flags == 0)
            return true;
        if ((flags & NativeMethods.LWA_ALPHA) != 0 && alpha == 0)
            return true;
        if ((flags & NativeMethods.LWA_ALPHA) == 0 && (flags & NativeMethods.LWA_COLORKEY) != 0)
            return true;

        return false;
    }

    private static bool RectanglesIntersect(NativeMethods.RECT a, NativeMethods.RECT b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    private bool WindowIntersectsAnyMonitor(NativeMethods.RECT windowRect)
    {
        for (int i = 0; i < _monitorRects.Count; i++)
        {
            if (RectanglesIntersect(windowRect, _monitorRects[i]))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Cheap, no-allocation predicate run before any string read or process query.
    /// Returns the queried extended window style via <paramref name="exStyle"/> so callers
    /// can reuse it (e.g. to test <see cref="NativeMethods.WS_EX_TOPMOST"/>) without a
    /// second GetWindowLongPtr round-trip.
    /// </summary>
    private bool PassesCheapFilters(IntPtr hWnd, out long exStyle, out IntPtr keptOwner)
    {
        exStyle = 0;
        keptOwner = IntPtr.Zero;
        if (hWnd == _shellWindow || !NativeMethods.IsWindowVisible(hWnd))
            return false;
        if (IsCloaked(hWnd))
            return false;

        exStyle = (long)NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE);

        if (IsFullyTransparentLayered(hWnd, exStyle))
            return false;

        bool isAppWindow = (exStyle & NativeMethods.WS_EX_APPWINDOW) != 0;

        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0 && !isAppWindow)
            return false;

        if ((exStyle & NativeMethods.WS_EX_NOACTIVATE) != 0 && !isAppWindow)
            return false;

        bool isIconic = NativeMethods.IsIconic(hWnd);

        if (!isIconic && !HasValidWindowSize(hWnd))
            return false;

        if (!isIconic && _monitorRects.Count > 0 &&
            NativeMethods.GetWindowRect(hWnd, out var windowRect) &&
            !WindowIntersectsAnyMonitor(windowRect))
            return false;

        // Ownership chain.
        var owner = NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER);
        if (owner != IntPtr.Zero && !isAppWindow)
        {
            var current = owner;
            while (current != IntPtr.Zero)
            {
                if (NativeMethods.IsWindowVisible(current))
                {
                    // Delphi VCL apps (Navicat, EmEditor, older DBeaver builds, etc.) anchor
                    // every TForm to a hidden TApplication window, which means user-facing
                    // splash screens, login dialogs and trial nag prompts all appear as owned
                    // windows with a visible ancestor and would otherwise be silently dropped.
                    // Treat owned windows that look like real dialogs (have a caption text or
                    // a dialog frame style) as user-facing and let them through.
                    if (IsLikelyUserOwnedDialog(hWnd, exStyle))
                    {
                        // Remember the visible owner. GetWindows() drops this dialog if the owner
                        // itself ends up in the switcher list (modal child like System Properties →
                        // Environment Variables). When the owner is a hidden TApplication proxy
                        // (Delphi VCL) it never appears in the list, so the dialog is kept.
                        keptOwner = current;
                        break;
                    }
                    return false;
                }
                current = NativeMethods.GetWindow(current, NativeMethods.GW_OWNER);
            }
        }

        return true;
    }

    /// <summary>
    /// Heuristic for "owned but user-facing" windows. Used to bypass the owner-chain check
    /// for Delphi VCL splash forms / trial dialogs that otherwise look like internal helpers.
    /// Strict: must either carry a non-empty caption, or have <c>WS_EX_DLGMODALFRAME</c>
    /// (which Delphi sets on every <c>TForm</c> styled as a dialog). This deliberately rejects
    /// owned helper forms with no caption and no dialog frame (e.g. Navicat's
    /// <c>TImageCollection*Form</c>).
    /// </summary>
    private static bool IsLikelyUserOwnedDialog(IntPtr hWnd, long exStyle)
    {
        if ((exStyle & WS_EX_DLGMODALFRAME) != 0)
            return true;
        return NativeMethods.GetWindowTextLength(hWnd) > 0;
    }

    private WindowInfo? TryGetWindowInfo(IntPtr hWnd)
    {
        if (!PassesCheapFilters(hWnd, out var exStyle, out var keptOwner))
            return null;

        // Title pulled before process query — windows with no title are noise (e.g. invisible
        // tooltip helpers with no caption) and we can drop them without paying for OpenProcess.
        // Exception: Delphi VCL trial dialogs (e.g. Navicat's TRegistrationSubForm) sometimes
        // ship with no caption text but carry WS_EX_DLGMODALFRAME — those are real, user-facing
        // dialogs and we keep them; AppWindow.FormattedTitle falls back to ProcessName.
        var titleLength = NativeMethods.GetWindowTextLength(hWnd);
        bool hasDlgFrame = (exStyle & WS_EX_DLGMODALFRAME) != 0;
        if (titleLength == 0 && !hasDlgFrame)
            return null;

        // Class name short-circuits known UI shells before process query.
        _classBuilder.Clear();
        _classBuilder.EnsureCapacity(MaxClassNameLength);
        NativeMethods.GetClassName(hWnd, _classBuilder, _classBuilder.Capacity);
        var className = _classBuilder.ToString();
        if (ExcludedClassNames.Contains(className))
            return null;

        NativeMethods.GetWindowThreadProcessId(hWnd, out uint processId);
        if (processId == _currentProcessId)
            return null;

        if (!_processNameCache.TryGetValue(processId, out var processName))
        {
            processName = GetProcessName(processId);
            _processNameCache[processId] = processName;
        }
        if (ExcludedProcessNames.Contains(processName))
            return null;

        // Now read the title (cheapest done after we know we'll keep the window).
        string title;
        if (titleLength > 0)
        {
            _titleBuilder.Clear();
            _titleBuilder.EnsureCapacity(titleLength + 1);
            NativeMethods.GetWindowText(hWnd, _titleBuilder, _titleBuilder.Capacity);
            title = _titleBuilder.ToString();
            if (string.IsNullOrWhiteSpace(title))
            {
                // Whitespace-only titles are still noise unless this is a dialog-frame window —
                // in which case AppWindow.FormattedTitle will fall back to ProcessName.
                if (!hasDlgFrame) return null;
                title = string.Empty;
            }
        }
        else
        {
            // Title-less dialog-frame window — display falls back to ProcessName via FormattedTitle.
            title = string.Empty;
        }

        // WS_EX_TOPMOST windows (browser Picture-in-Picture, always-on-top utilities, etc.)
        // are forced ahead of all non-topmost windows in the OS Z-order. EnumWindows therefore
        // returns them first, which would otherwise pin them to the head of our switcher list
        // and break the MRU illusion. Flag them so GetWindows() can sink them to the bottom.
        bool isTopmost = (exStyle & NativeMethods.WS_EX_TOPMOST) != 0;

        return new WindowInfo(title, className, processId, processName, isTopmost, keptOwner);
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

    private bool MonitorEnumCallback(IntPtr hMon, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data)
    {
        _monitors.Add(hMon);
        var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (NativeMethods.GetMonitorInfo(hMon, ref mi))
            _monitorRects.Add(mi.rcMonitor);
        else
            _monitorRects.Add(rect);
        return true;
    }

    private bool EnumWindowsCallback(IntPtr hWnd, IntPtr lParam)
    {
        try
        {
            var info = TryGetWindowInfo(hWnd);
            if (info is null)
                return true;

            _seenHandles.Add(hWnd);
            _seenProcessIds.Add(info.Value.ProcessId);

            // Track owned dialogs that were kept despite a visible owner, so GetWindows() can
            // drop them post-enumeration when the owner is itself shown (see _ownedDialogOwners).
            if (info.Value.OwnerHandle != IntPtr.Zero)
                _ownedDialogOwners[hWnd] = info.Value.OwnerHandle;

            // Topmost windows are routed to a separate bucket and appended at the tail of
            // the final list (see GetWindows()). EnumWindows reports them first regardless
            // of MRU, so leaving them in _windowsScratch would always pin them to position 0.
            var bucket = info.Value.IsTopmost ? _topmostScratch : _windowsScratch;

            // Reuse existing AppWindow instance if title/state hasn't changed.
            // This preserves the async-loaded Icon and avoids triggering UI rebinds.
            if (_windowInstanceCache.TryGetValue(hWnd, out var existing) &&
                existing.Title == info.Value.Title &&
                existing.ProcessName == info.Value.ProcessName)
            {
                var (existingMin, existingMax) = (existing.IsMinimized, existing.IsMaximized);
                var (curMin, curMax) = GetWindowState(hWnd);
                if (existingMin == curMin && existingMax == curMax)
                {
                    bucket.Add(existing);
                    return true;
                }
            }

            var (isMinimized, isMaximized) = GetWindowState(hWnd);
            var window = new AppWindow(hWnd, info.Value.Title, info.Value.ClassName,
                info.Value.ProcessId, info.Value.ProcessName, isMinimized, isMaximized,
                _monitors, _elevationCache);

            // Note: we deliberately do not pre-populate the icon from the per-exe cache here.
            // Several distinct windows can share an executable but have different icons —
            // most notably File Explorer and Control Panel both run in explorer.exe and would
            // alias each other if pre-filled. AppWindow loads its own icon asynchronously and
            // only consults the per-exe cache via paths that are genuinely exe-wide
            // (SHGetFileInfo, ExtractAssociatedIcon, UWP manifest).

            _windowInstanceCache[hWnd] = window;
            bucket.Add(window);
        }
        catch
        {
            // Skip windows that cause errors.
        }

        return true;
    }

    public List<AppWindow> GetWindows()
    {
        _shellWindow = NativeMethods.GetShellWindow();
        _currentProcessId = (uint)Environment.ProcessId;

        _monitors.Clear();
        _monitorRects.Clear();
        _windowsScratch.Clear();
        _topmostScratch.Clear();
        _seenHandles.Clear();
        _seenProcessIds.Clear();
        _ownedDialogOwners.Clear();

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, _monitorEnumProc, IntPtr.Zero);
        NativeMethods.EnumWindows(_enumWindowsProc, IntPtr.Zero);

        // Drop instance-cache entries for windows that no longer exist (closed since last call).
        if (_windowInstanceCache.Count > _seenHandles.Count)
        {
            var stale = new List<IntPtr>(_windowInstanceCache.Count - _seenHandles.Count);
            foreach (var key in _windowInstanceCache.Keys)
            {
                if (!_seenHandles.Contains(key))
                    stale.Add(key);
            }
            foreach (var key in stale)
                _windowInstanceCache.Remove(key);
        }

        // Drop process-name / elevation entries for processes that no longer have any visible window.
        // Keeps the caches bounded across long sessions.
        if (_processNameCache.Count > _seenProcessIds.Count * 2)
        {
            var staleP = new List<uint>();
            foreach (var pid in _processNameCache.Keys)
            {
                if (!_seenProcessIds.Contains(pid))
                    staleP.Add(pid);
            }
            foreach (var pid in staleP)
            {
                _processNameCache.Remove(pid);
                _elevationCache.Remove(pid);
            }
            IconCacheService.Instance.TrimProcessCache(_seenProcessIds);
        }

        // Compose the final list: normal Z-ordered windows first (preserving MRU semantics),
        // then topmost windows at the tail. Relative order within each bucket matches the
        // EnumWindows traversal, so multiple PIP/utility windows keep a stable order.
        // Owned dialogs whose owner is also displayed (e.g. System Properties → Environment
        // Variables) are redundant modal children and dropped here; owned dialogs of Delphi VCL
        // apps survive because their visible owner (a hidden TApplication proxy) is not displayed.
        var result = new List<AppWindow>(_windowsScratch.Count + _topmostScratch.Count);
        foreach (var w in _windowsScratch)
        {
            if (!IsRedundantOwnedDialog(w.Handle))
                result.Add(w);
        }
        foreach (var w in _topmostScratch)
        {
            if (!IsRedundantOwnedDialog(w.Handle))
                result.Add(w);
        }
        return result;
    }

    /// <summary>
    /// True when <paramref name="hWnd"/> is an owned dialog that was kept via the Delphi-VCL
    /// compatibility path AND its visible owner is itself present in the switcher list. Such a
    /// dialog (e.g. the "Environment Variables" window owned by the visible "System Properties"
    /// window) is a redundant modal child and should not occupy a separate switcher entry.
    /// </summary>
    private bool IsRedundantOwnedDialog(IntPtr hWnd) =>
        _ownedDialogOwners.TryGetValue(hWnd, out var owner) && _seenHandles.Contains(owner);

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

    private const int MinWindowSize = 50;
    /// <summary>
    /// When a window is collapsed to a title-bar strip (abnormal layout / user resize),
    /// <see cref="NativeMethods.GetWindowRect"/> height is often ~30–45px, below <see cref="MinWindowSize"/>.
    /// </summary>
    private const int MinCaptionStripHeight = 28;

    private static bool HasValidWindowSize(IntPtr hWnd)
    {
        try
        {
            if (!NativeMethods.GetWindowRect(hWnd, out var rect))
                return false;

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width >= MinWindowSize && height >= MinWindowSize)
                return true;

            var style = (long)NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_STYLE);
            var hasCaption = (style & NativeMethods.WS_CAPTION) == NativeMethods.WS_CAPTION;
            if (hasCaption && width >= MinWindowSize && height >= MinCaptionStripHeight)
                return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string GetProcessName(uint processId)
    {
        var hProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (hProcess == IntPtr.Zero)
            return "Unknown";
        try
        {
            var buffer = new StringBuilder(260);
            int size = buffer.Capacity;
            if (NativeMethods.QueryFullProcessImageName(hProcess, 0, buffer, ref size))
                return System.IO.Path.GetFileNameWithoutExtension(buffer.ToString());
            return "Unknown";
        }
        finally
        {
            NativeMethods.CloseHandle(hProcess);
        }
    }
}
