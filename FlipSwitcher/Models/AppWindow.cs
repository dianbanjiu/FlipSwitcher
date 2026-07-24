using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using FlipSwitcher.Core;
using FlipSwitcher.Services;

namespace FlipSwitcher.Models;

/// <summary>
/// Represents a window that can be switched to.
/// Icons and pinyin transliterations are resolved through global caches
/// (<see cref="IconCacheService"/>, <see cref="PinyinService"/>) so that
/// re-creating <see cref="AppWindow"/> instances on every refresh stays cheap.
/// </summary>
public class AppWindow : INotifyPropertyChanged
{
    private bool _isSelected;
    private ImageSource? _icon;
    private bool _iconLoading;
    private bool? _isElevated;
    private int? _monitorNumber;
    private readonly List<IntPtr>? _monitors;
    private readonly Dictionary<uint, bool>? _elevationCache;

    public IntPtr Handle { get; }
    public string Title { get; }
    public string ProcessName { get; }
    public string ClassName { get; }
    public uint ProcessId { get; }
    public bool IsMinimized { get; }
    public bool IsMaximized { get; }

    /// <summary>
    /// Whether the window's process is running with administrator privileges.
    /// </summary>
    public bool IsElevated
    {
        get
        {
            if (_isElevated == null)
            {
                if (_elevationCache != null && _elevationCache.TryGetValue(ProcessId, out var cached))
                    _isElevated = cached;
                else
                {
                    _isElevated = CheckProcessElevation();
                    _elevationCache?.TryAdd(ProcessId, _isElevated.Value);
                }
            }
            return _isElevated.Value;
        }
    }

    /// <summary>
    /// The monitor number (1-based) where this window is located.
    /// </summary>
    public int MonitorNumber
    {
        get
        {
            _monitorNumber ??= GetMonitorNumber();
            return _monitorNumber.Value;
        }
    }

    private int GetMonitorNumber()
    {
        var hMonitor = NativeMethods.MonitorFromWindow(Handle, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (hMonitor == IntPtr.Zero) return 1;

        if (_monitors != null)
        {
            int index = _monitors.IndexOf(hMonitor);
            return index >= 0 ? index + 1 : 1;
        }

        // Fallback: enumerate independently (should not reach here normally).
        var monitors = new List<IntPtr>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMon, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data) =>
        {
            monitors.Add(hMon);
            return true;
        }, IntPtr.Zero);
        int idx = monitors.IndexOf(hMonitor);
        return idx >= 0 ? idx + 1 : 1;
    }

    private bool CheckProcessElevation()
    {
        var hProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, ProcessId);
        if (hProcess == IntPtr.Zero) return false;

        try
        {
            if (!NativeMethods.OpenProcessToken(hProcess, NativeMethods.TOKEN_QUERY, out var tokenHandle))
                return false;

            try
            {
                var elevationSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.TOKEN_ELEVATION>();
                var elevationPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(elevationSize);
                try
                {
                    if (NativeMethods.GetTokenInformation(tokenHandle, NativeMethods.TokenElevation, elevationPtr, elevationSize, out _))
                    {
                        var elevation = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.TOKEN_ELEVATION>(elevationPtr);
                        return elevation.TokenIsElevated != 0;
                    }
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(elevationPtr);
                }
            }
            finally
            {
                NativeMethods.CloseHandle(tokenHandle);
            }
        }
        catch
        {
            // Assume normal privileges if detection fails.
        }
        finally
        {
            NativeMethods.CloseHandle(hProcess);
        }
        return false;
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Icon for this window. Loaded asynchronously on first access; subsequent gets return
    /// the cached value on the instance. Because <see cref="Services.WindowService"/> reuses
    /// AppWindow instances across refreshes, the icon survives switcher re-opens.
    /// </summary>
    public ImageSource? Icon
    {
        get
        {
            if (_icon != null) return _icon;
            if (_iconLoading) return null;

            // Note: we deliberately do NOT consult the global IconCacheService here. The fast-path
            // would incorrectly hit for windows that share an executable but have distinct icons
            // (e.g. File Explorer, Control Panel and the Recycle Bin all run inside explorer.exe
            // but expose different icons via WM_GETICON). LoadIcon() consults the cache only for
            // the genuinely shared per-exe shell icon — never for per-window icons.
            _iconLoading = true;
            _ = LoadIconAsync();
            return null;
        }
    }

    /// <summary>
    /// Pre-populate the icon from a known cached value (used by <see cref="Services.WindowService"/>
    /// when an existing AppWindow instance is being reused). Should NOT be called with a value
    /// derived from <c>WM_GETICON</c> on a different window.
    /// </summary>
    internal void TrySetCachedIcon(ImageSource? icon)
    {
        if (icon == null || _icon != null) return;
        _icon = icon;
    }

    private async Task LoadIconAsync()
    {
        var icon = await Task.Run(LoadIcon);

        if (icon != null)
        {
            _icon = icon;
            Application.Current?.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => OnPropertyChanged(nameof(Icon))));
        }
        _iconLoading = false;
    }

    public string FormattedTitle => string.IsNullOrWhiteSpace(Title) ? ProcessName : Title;

    public AppWindow(IntPtr handle, string title, string className, uint processId, string processName,
        bool isMinimized, bool isMaximized,
        List<IntPtr>? monitors = null, Dictionary<uint, bool>? elevationCache = null)
    {
        Handle = handle;
        Title = title;
        ClassName = className;
        ProcessId = processId;
        ProcessName = processName;
        IsMinimized = isMinimized;
        IsMaximized = isMaximized;
        _monitors = monitors;
        _elevationCache = elevationCache;
    }

    private const uint IconTimeoutMs = 50;

    private bool IsUwpWindow => ClassName == "ApplicationFrameWindow";

    /// <summary>
    /// Cache key used for HWND-specific icons (those obtained via WM_GETICON that may differ from
    /// the per-exe icon — e.g. document-specific icons in some IDEs). Falls back to exe-keyed cache.
    /// </summary>
    private string GetWindowIconCacheKey() => $"hwnd:{Handle.ToInt64()}";

    // Get icon handle via window messages (may be document-specific).
    private IntPtr GetWindowIconHandle()
    {
        // Prefer ICON_BIG, skip ICON_SMALL2 (rarely used).
        NativeMethods.SendMessageTimeout(Handle, NativeMethods.WM_GETICON, (IntPtr)NativeMethods.ICON_BIG, IntPtr.Zero,
            NativeMethods.SMTO_ABORTIFHUNG, IconTimeoutMs, out var h);
        if (h != IntPtr.Zero) return h;

        NativeMethods.SendMessageTimeout(Handle, NativeMethods.WM_GETICON, (IntPtr)NativeMethods.ICON_SMALL, IntPtr.Zero,
            NativeMethods.SMTO_ABORTIFHUNG, IconTimeoutMs, out h);
        if (h != IntPtr.Zero) return h;

        h = NativeMethods.GetClassLongPtr(Handle, NativeMethods.GCL_HICON);
        return h != IntPtr.Zero ? h : NativeMethods.GetClassLongPtr(Handle, NativeMethods.GCL_HICONSM);
    }

    /// <summary>
    /// UWP icon loading. Manifest parsing and suffix probing are cached in <see cref="IconCacheService"/>.
    /// </summary>
    private ImageSource? LoadUwpIcon()
    {
        var iconCache = IconCacheService.Instance;

        // Get the real UWP process ID (try multiple child window classes).
        uint uwpPid = ProcessId;
        string[] childClasses = ["Windows.UI.Core.CoreWindow", "Windows.UI.Composition.DesktopWindowContentBridge"];
        foreach (var cls in childClasses)
        {
            var childHwnd = NativeMethods.FindWindowEx(Handle, IntPtr.Zero, cls, null);
            if (childHwnd != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(childHwnd, out uint childPid);
                if (childPid != 0 && childPid != ProcessId)
                {
                    uwpPid = childPid;
                    break;
                }
            }
        }

        var exePath = iconCache.GetProcessPath(uwpPid);
        if (string.IsNullOrEmpty(exePath)) return null;

        // Cache key for UWP is the app directory (manifest is per-package).
        var appDir = Path.GetDirectoryName(exePath);
        if (!string.IsNullOrEmpty(appDir))
        {
            if (iconCache.TryGetExeIcon(appDir, out var cached) && cached != null)
                return cached;

            var icon = iconCache.LoadIconFromAppxManifest(appDir);
            if (icon != null)
            {
                iconCache.SetExeIcon(appDir, icon);
                return icon;
            }
        }

        // Fallback: Shell API (cached internally against exePath).
        return iconCache.LoadIconFromShell(exePath);
    }

    private ImageSource? LoadIcon()
    {
        var iconCache = IconCacheService.Instance;
        var exePath = iconCache.GetProcessPath(ProcessId);

        // UWP apps use a dedicated path. UWP icons are package-wide so cache by app dir.
        if (IsUwpWindow)
        {
            var uwpIcon = LoadUwpIcon();
            if (uwpIcon != null) return uwpIcon;
        }

        // 1. Window icon handle (WM_GETICON / GCL_HICON).
        // This may be a per-window icon (e.g. File Explorer's folder icon vs. Control Panel's
        // gear icon — both running inside explorer.exe). NEVER write this into the per-exe cache,
        // or the icons of every window in the process get cross-contaminated.
        var iconHandle = GetWindowIconHandle();
        if (iconHandle != IntPtr.Zero)
        {
            var icon = IconCacheService.IconHandleToImageSource(iconHandle);
            if (icon != null)
                return icon;
        }

        // 2. Shell API — this returns the icon associated with the executable on disk, which
        // is genuinely shared across all windows of the same exe. Safe (and beneficial) to cache.
        if (!string.IsNullOrEmpty(exePath))
        {
            var icon = iconCache.LoadIconFromShell(exePath); // caches internally
            if (icon != null) return icon;
        }

        // 3. Extract from process module (last resort). Also exe-wide; safe to cache.
        try
        {
            if (!string.IsNullOrEmpty(exePath))
            {
                using var ico = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (ico != null)
                {
                    var icon = IconCacheService.IconHandleToImageSource(ico.Handle);
                    if (icon != null)
                        iconCache.SetExeIcon(exePath, icon);
                    return icon;
                }
            }
        }
        catch { }

        return null;
    }

    public bool MatchesFilter(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        if (Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            return true;

        if (Services.SettingsService.Instance.Settings.EnablePinyinSearch)
        {
            // Pinyin caches live in PinyinService and are keyed by the original string,
            // so they survive across AppWindow re-instantiation (window list refresh).
            var pinyin = Services.PinyinService.Instance;
            var lowerFilter = filter.ToLowerInvariant();

            if (pinyin.GetPinyinInitials(Title).Contains(lowerFilter, StringComparison.Ordinal))
                return true;
            if (pinyin.GetFullPinyin(Title).Contains(lowerFilter, StringComparison.Ordinal))
                return true;
            if (pinyin.GetPinyinInitials(ProcessName).Contains(lowerFilter, StringComparison.Ordinal))
                return true;
            if (pinyin.GetFullPinyin(ProcessName).Contains(lowerFilter, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Close this window by sending WM_CLOSE.
    /// </summary>
    /// <remarks>
    /// Special case: when this window owns an active modal dialog (e.g. System Properties showing
    /// its "Environment Variables" dialog), the root window is <c>EnableWindow(FALSE)</c>-disabled
    /// and WM_CLOSE posted to it is silently ignored — the window would never actually close while
    /// the switcher optimistically removed it from the list. In that case we redirect WM_CLOSE to
    /// the active popup (the dialog the user actually sees) so the close takes effect.
    /// </remarks>
    /// <returns>
    /// <c>true</c> if the root window itself was targeted (the caller may remove it from the list);
    /// <c>false</c> if only an owned modal dialog was dismissed and the root window remains open.
    /// </returns>
    public bool Close()
    {
        try
        {
            // GW_ENABLEDPOPUP returns the active (enabled) popup owned by this window, or the
            // window itself when there is none. See <remarks> for the modal-dialog rationale.
            var popup = NativeMethods.GetWindow(Handle, NativeMethods.GW_ENABLEDPOPUP);
            bool hasModalPopup =
                popup != IntPtr.Zero &&
                popup != Handle &&
                NativeMethods.IsWindowVisible(popup);

            var target = hasModalPopup ? popup : Handle;
            NativeMethods.PostMessage(target, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

            return !hasModalPopup;
        }
        catch
        {
            // On failure assume the root was targeted so list/intent stay consistent.
            return true;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
