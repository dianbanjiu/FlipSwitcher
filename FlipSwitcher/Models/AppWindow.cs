using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using FlipSwitcher.Core;

namespace FlipSwitcher.Models;

/// <summary>
/// Represents a window that can be switched to
/// </summary>
public class AppWindow : INotifyPropertyChanged
{
    private bool _isSelected;
    private ImageSource? _icon;
    private bool _iconLoading;
    private bool? _isElevated;
    private int? _monitorNumber;
    private readonly System.Collections.Generic.List<IntPtr>? _monitors;
    private readonly System.Collections.Generic.Dictionary<uint, bool>? _elevationCache;
    private string? _titlePinyinInitials;
    private string? _titleFullPinyin;
    private string? _processNamePinyinInitials;
    private string? _processNameFullPinyin;

    public IntPtr Handle { get; }
    public string Title { get; }
    public string ProcessName { get; }
    public string ClassName { get; }
    public uint ProcessId { get; }
    public bool IsMinimized { get; }
    public bool IsMaximized { get; }

    /// <summary>
    /// Whether the window's process is running with administrator privileges
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
    /// The monitor number (1-based) where this window is located
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

        // Fallback: enumerate independently (should not reach here)
        var monitors = new System.Collections.Generic.List<IntPtr>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data) =>
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
            // Assume normal privileges if detection fails
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

    public ImageSource? Icon
    {
        get
        {
            if (_icon == null && !_iconLoading)
            {
                _iconLoading = true;
                _ = LoadIconAsync();
            }
            return _icon;
        }
    }

    private async Task LoadIconAsync()
    {
        var icon = await Task.Run(() =>
        {
            var result = LoadIcon();
            if (result is { IsFrozen: false, CanFreeze: true })
                result.Freeze();
            return result;
        });

        if (icon != null)
        {
            _icon = icon;
            Application.Current?.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => OnPropertyChanged(nameof(Icon))));
        }
    }

    public string FormattedTitle => string.IsNullOrWhiteSpace(Title) ? ProcessName : Title;

    public AppWindow(IntPtr handle, string title, string className, uint processId, string processName, bool isMinimized, bool isMaximized, System.Collections.Generic.List<IntPtr>? monitors = null, System.Collections.Generic.Dictionary<uint, bool>? elevationCache = null)
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

    // Convert icon handle to ImageSource
    private static ImageSource? IconHandleToImageSource(IntPtr iconHandle, bool destroyAfter = false)
    {
        if (iconHandle == IntPtr.Zero) return null;
        try
        {
            using var icon = System.Drawing.Icon.FromHandle(iconHandle);
            using var cloned = (System.Drawing.Icon)icon.Clone();
            var source = Imaging.CreateBitmapSourceFromHIcon(cloned.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            if (source is { IsFrozen: false, CanFreeze: true })
                source.Freeze();
            return source;
        }
        catch { return null; }
        finally { if (destroyAfter) NativeMethods.DestroyIcon(iconHandle); }
    }

    // Get icon handle via window messages
    private IntPtr GetWindowIconHandle()
    {
        // Prefer ICON_BIG, skip ICON_SMALL2 (rarely used)
        NativeMethods.SendMessageTimeout(Handle, NativeMethods.WM_GETICON, (IntPtr)NativeMethods.ICON_BIG, IntPtr.Zero,
            NativeMethods.SMTO_ABORTIFHUNG, IconTimeoutMs, out var h);
        if (h != IntPtr.Zero) return h;

        NativeMethods.SendMessageTimeout(Handle, NativeMethods.WM_GETICON, (IntPtr)NativeMethods.ICON_SMALL, IntPtr.Zero,
            NativeMethods.SMTO_ABORTIFHUNG, IconTimeoutMs, out h);
        if (h != IntPtr.Zero) return h;

        h = NativeMethods.GetClassLongPtr(Handle, NativeMethods.GCL_HICON);
        return h != IntPtr.Zero ? h : NativeMethods.GetClassLongPtr(Handle, NativeMethods.GCL_HICONSM);
    }

    // Get process executable path
    private string? GetProcessPath(uint processId)
    {
        var hProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (hProcess == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(260);
            int size = buffer.Capacity;
            return NativeMethods.QueryFullProcessImageName(hProcess, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally { NativeMethods.CloseHandle(hProcess); }
    }

    // Load icon via Shell API (works for all apps including UWP)
    private ImageSource? LoadIconFromShell(string filePath)
    {
        var shinfo = new NativeMethods.SHFILEINFO();
        var result = NativeMethods.SHGetFileInfo(filePath, 0, ref shinfo,
            (uint)System.Runtime.InteropServices.Marshal.SizeOf(shinfo),
            NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON);
        return result != 0 ? IconHandleToImageSource(shinfo.hIcon, destroyAfter: true) : null;
    }

    // Load icon from image file (UWP manifest resource)
    private ImageSource? LoadIconFromImageFile(string imagePath)
    {
        if (!File.Exists(imagePath)) return null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 48;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    // UWP: load icon from AppxManifest
    private ImageSource? LoadIconFromAppxManifest(string appDir)
    {
        var manifestPath = Path.Combine(appDir, "AppxManifest.xml");
        if (!File.Exists(manifestPath)) return null;

        try
        {
            var doc = XDocument.Load(manifestPath);
            var visualElements = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "VisualElements");
            
            // Try multiple logo attributes
            string?[] logoAttrs = [
                visualElements?.Attribute("Square44x44Logo")?.Value,
                visualElements?.Attribute("Square150x150Logo")?.Value,
                visualElements?.Attribute("Square71x71Logo")?.Value
            ];
            
            var logoPath = logoAttrs.FirstOrDefault(p => !string.IsNullOrEmpty(p));
            if (string.IsNullOrEmpty(logoPath))
            {
                var logoElement = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Logo" && e.Parent?.Name.LocalName == "Properties");
                logoPath = logoElement?.Value;
            }
            if (string.IsNullOrEmpty(logoPath)) return null;

            var baseLogoPath = Path.Combine(appDir, logoPath);
            var logoDir = Path.GetDirectoryName(baseLogoPath);
            var logoName = Path.GetFileNameWithoutExtension(baseLogoPath);
            var logoExt = Path.GetExtension(baseLogoPath);

            if (string.IsNullOrEmpty(logoDir)) return null;

            // Try common variants (prefer larger sizes and unplated)
            string[] suffixes = [
                ".targetsize-256_altform-unplated", ".targetsize-256",
                ".targetsize-64_altform-unplated", ".targetsize-64",
                ".targetsize-48_altform-unplated", ".targetsize-48",
                ".targetsize-32_altform-unplated", ".targetsize-32",
                ".scale-200", ".scale-100", ""
            ];
            foreach (var suffix in suffixes)
            {
                var icon = LoadIconFromImageFile(Path.Combine(logoDir, $"{logoName}{suffix}{logoExt}"));
                if (icon != null) return icon;
            }
            return LoadIconFromImageFile(baseLogoPath);
        }
        catch { return null; }
    }

    // UWP icon loading
    private ImageSource? LoadUwpIcon()
    {
        // Get the real UWP process ID (try multiple child window classes)
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

        var exePath = GetProcessPath(uwpPid);
        if (string.IsNullOrEmpty(exePath)) return null;

        var appDir = Path.GetDirectoryName(exePath);
        
        // Prefer manifest (more accurate)
        if (!string.IsNullOrEmpty(appDir))
        {
            var icon = LoadIconFromAppxManifest(appDir);
            if (icon != null) return icon;
        }

        // Fallback: Shell API
        return LoadIconFromShell(exePath);
    }

    private ImageSource? LoadIcon()
    {
        // UWP apps use a dedicated path
        if (IsUwpWindow)
        {
            var uwpIcon = LoadUwpIcon();
            if (uwpIcon != null) return uwpIcon;
        }

        // 1. Window icon handle (fastest)
        var iconHandle = GetWindowIconHandle();
        if (iconHandle != IntPtr.Zero)
        {
            var icon = IconHandleToImageSource(iconHandle);
            if (icon != null) return icon;
        }

        // 2. Shell API (reliable)
        var exePath = GetProcessPath(ProcessId);
        if (!string.IsNullOrEmpty(exePath))
        {
            var icon = LoadIconFromShell(exePath);
            if (icon != null) return icon;
        }

        // 3. Extract from process module (last resort)
        try
        {
            if (!string.IsNullOrEmpty(exePath))
            {
                using var ico = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (ico != null) return IconHandleToImageSource(ico.Handle);
            }
        }
        catch { }

        return null;
    }

    public void Activate()
    {
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        uint foregroundThreadId = foregroundWindow != IntPtr.Zero
            ? NativeMethods.GetWindowThreadProcessId(foregroundWindow, out _)
            : 0;
        var currentThreadId = NativeMethods.GetCurrentThreadId();
        var targetThreadId = NativeMethods.GetWindowThreadProcessId(Handle, out _);

        // Avoid modifying global SPI_SETFOREGROUNDLOCKTIMEOUT to prevent permanently altering system behavior on crash
        NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
        NativeMethods.LockSetForegroundWindow(NativeMethods.LSFW_UNLOCK);

        bool attachedToForeground = false;
        bool attachedToTarget = false;

        try
        {
            if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
            {
                attachedToForeground = NativeMethods.AttachThreadInput(currentThreadId, foregroundThreadId, true);
            }

            if (targetThreadId != 0 && targetThreadId != currentThreadId && targetThreadId != foregroundThreadId)
            {
                attachedToTarget = NativeMethods.AttachThreadInput(currentThreadId, targetThreadId, true);
            }

            var placement = new NativeMethods.WINDOWPLACEMENT();
            placement.length = System.Runtime.InteropServices.Marshal.SizeOf(placement);
            NativeMethods.GetWindowPlacement(Handle, ref placement);
            
            bool wasMaximized = placement.showCmd == NativeMethods.SW_SHOWMAXIMIZED_PLACEMENT || 
                                NativeMethods.IsZoomed(Handle) || 
                                IsMaximized;
            bool wasMinimized = placement.showCmd == NativeMethods.SW_SHOWMINIMIZED || 
                                NativeMethods.IsIconic(Handle) || 
                                IsMinimized;

            if (wasMinimized)
            {
                NativeMethods.ShowWindow(Handle, wasMaximized
                    ? NativeMethods.SW_SHOWMAXIMIZED
                    : NativeMethods.SW_RESTORE);
            }
            else if (wasMaximized)
            {
                NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWMAXIMIZED);
            }

            NativeMethods.BringWindowToTop(Handle);
            NativeMethods.SetForegroundWindow(Handle);

            if (NativeMethods.GetForegroundWindow() != Handle)
            {
                NativeMethods.keybd_event(NativeMethods.VK_ALT, 0, NativeMethods.KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
                NativeMethods.keybd_event(NativeMethods.VK_ALT, 0, NativeMethods.KEYEVENTF_EXTENDEDKEY | NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);

                NativeMethods.SetForegroundWindow(Handle);
                NativeMethods.BringWindowToTop(Handle);
            }

            if (NativeMethods.GetForegroundWindow() != Handle)
            {
                NativeMethods.SwitchToThisWindow(Handle, true);
            }

            if (NativeMethods.GetForegroundWindow() != Handle)
            {
                NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW);
                NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW);
            }
        }
        catch
        {
            try
            {
                if (NativeMethods.IsIconic(Handle))
                {
                    NativeMethods.ShowWindow(Handle, NativeMethods.SW_RESTORE);
                }
                NativeMethods.SwitchToThisWindow(Handle, true);
            }
            catch
            {
                // Ignore all errors in fallback
            }
        }
        finally
        {
            if (attachedToForeground)
            {
                NativeMethods.AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }
            if (attachedToTarget)
            {
                NativeMethods.AttachThreadInput(currentThreadId, targetThreadId, false);
            }
        }
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
            var lowerFilter = filter.ToLowerInvariant();

            _titlePinyinInitials ??= Services.PinyinService.Instance.GetPinyinInitials(Title).ToLowerInvariant();
            if (_titlePinyinInitials.Contains(lowerFilter))
                return true;

            _titleFullPinyin ??= Services.PinyinService.Instance.GetFullPinyin(Title).ToLowerInvariant();
            if (_titleFullPinyin.Contains(lowerFilter))
                return true;

            _processNamePinyinInitials ??= Services.PinyinService.Instance.GetPinyinInitials(ProcessName).ToLowerInvariant();
            if (_processNamePinyinInitials.Contains(lowerFilter))
                return true;

            _processNameFullPinyin ??= Services.PinyinService.Instance.GetFullPinyin(ProcessName).ToLowerInvariant();
            if (_processNameFullPinyin.Contains(lowerFilter))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Close this window by sending WM_CLOSE message
    /// </summary>
    public void Close()
    {
        try
        {
            NativeMethods.PostMessage(Handle, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // Ignore errors when closing window
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

