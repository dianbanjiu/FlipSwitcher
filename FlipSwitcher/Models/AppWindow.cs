using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
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
    private bool _iconLoaded;

    public IntPtr Handle { get; }
    public string Title { get; }
    public string ProcessName { get; }
    public string ClassName { get; }
    public uint ProcessId { get; }
    public bool IsMinimized { get; }
    public bool IsMaximized { get; }

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
            if (!_iconLoaded)
            {
                _icon = LoadIcon();
                _iconLoaded = true;
            }
            return _icon;
        }
    }

    public string FormattedTitle => string.IsNullOrWhiteSpace(Title) ? ProcessName : Title;

    public AppWindow(IntPtr handle, string title, string className, uint processId, string processName, bool isMinimized, bool isMaximized)
    {
        Handle = handle;
        Title = title;
        ClassName = className;
        ProcessId = processId;
        ProcessName = processName;
        IsMinimized = isMinimized;
        IsMaximized = isMaximized;
    }

    private const uint IconTimeoutMs = 100;

    private IntPtr GetWindowIconHandle()
    {
        IntPtr iconHandle = IntPtr.Zero;

        var iconSizes = new[] { NativeMethods.ICON_BIG, NativeMethods.ICON_SMALL, NativeMethods.ICON_SMALL2 };
        foreach (var iconSize in iconSizes)
        {
            NativeMethods.SendMessageTimeout(Handle, NativeMethods.WM_GETICON,
                (IntPtr)iconSize, IntPtr.Zero,
                NativeMethods.SMTO_ABORTIFHUNG, IconTimeoutMs, out iconHandle);
            if (iconHandle != IntPtr.Zero)
                return iconHandle;
        }

        iconHandle = NativeMethods.GetClassLongPtr(Handle, NativeMethods.GCL_HICON);
        if (iconHandle != IntPtr.Zero)
            return iconHandle;

        return NativeMethods.GetClassLongPtr(Handle, NativeMethods.GCL_HICONSM);
    }

    private ImageSource? LoadIconFromHandle(IntPtr iconHandle)
    {
        try
        {
            using var icon = System.Drawing.Icon.FromHandle(iconHandle);
            using var clonedIcon = (System.Drawing.Icon)icon.Clone();
            return Imaging.CreateBitmapSourceFromHIcon(
                clonedIcon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        catch
        {
            return null;
        }
    }

    private ImageSource? LoadIconFromProcess()
    {
        try
        {
            using var process = Process.GetProcessById((int)ProcessId);
            var mainModule = process.MainModule;
            if (mainModule?.FileName == null)
                return null;

            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(mainModule.FileName);
            if (icon == null)
                return null;

            return Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        catch
        {
            return null;
        }
    }

    private bool IsUwpWindow => ClassName == "ApplicationFrameWindow";

    private uint GetUwpChildProcessId()
    {
        try
        {
            // Find the actual UWP child window (Windows.UI.Core.CoreWindow)
            var childHwnd = NativeMethods.FindWindowEx(Handle, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
            if (childHwnd != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(childHwnd, out uint childProcessId);
                if (childProcessId != 0 && childProcessId != ProcessId)
                    return childProcessId;
            }
        }
        catch
        {
            // Ignore errors
        }
        return ProcessId;
    }

    private string? GetProcessPath(uint processId)
    {
        IntPtr hProcess = IntPtr.Zero;
        try
        {
            hProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (hProcess == IntPtr.Zero)
                return null;

            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            if (NativeMethods.QueryFullProcessImageName(hProcess, 0, buffer, ref size))
                return buffer.ToString();
        }
        catch
        {
            // Ignore errors
        }
        finally
        {
            if (hProcess != IntPtr.Zero)
                NativeMethods.CloseHandle(hProcess);
        }
        return null;
    }

    private ImageSource? LoadIconFromShell(string filePath)
    {
        try
        {
            var shinfo = new NativeMethods.SHFILEINFO();
            var result = NativeMethods.SHGetFileInfo(
                filePath, 0, ref shinfo, (uint)System.Runtime.InteropServices.Marshal.SizeOf(shinfo),
                NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON);

            if (result != 0 && shinfo.hIcon != IntPtr.Zero)
            {
                try
                {
                    using var icon = System.Drawing.Icon.FromHandle(shinfo.hIcon);
                    using var clonedIcon = (System.Drawing.Icon)icon.Clone();
                    return Imaging.CreateBitmapSourceFromHIcon(
                        clonedIcon.Handle,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                }
                finally
                {
                    NativeMethods.DestroyIcon(shinfo.hIcon);
                }
            }
        }
        catch
        {
            // Ignore errors
        }
        return null;
    }

    private ImageSource? LoadIconFromImageFile(string imagePath)
    {
        try
        {
            if (!File.Exists(imagePath))
                return null;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private ImageSource? LoadIconFromAppxManifest(string exePath)
    {
        try
        {
            var appDir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(appDir))
                return null;

            var manifestPath = Path.Combine(appDir, "AppxManifest.xml");
            if (!File.Exists(manifestPath))
                return null;

            var doc = XDocument.Load(manifestPath);
            var ns = doc.Root?.GetDefaultNamespace();
            var uapNs = doc.Root?.GetNamespaceOfPrefix("uap") 
                        ?? XNamespace.Get("http://schemas.microsoft.com/appx/manifest/uap/windows10");

            // Try to find logo from VisualElements - Square44x44Logo has best targetsize variants for icons
            var visualElements = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "VisualElements");

            string? logoPath = null;
            string? fallbackLogoPath = null;
            if (visualElements != null)
            {
                // Square44x44Logo typically has large targetsize variants (256, 128, etc.)
                logoPath = visualElements.Attribute("Square44x44Logo")?.Value;
                fallbackLogoPath = visualElements.Attribute("Square150x150Logo")?.Value
                                   ?? visualElements.Attribute("Square71x71Logo")?.Value;
            }

            // Fallback to Properties/Logo
            if (string.IsNullOrEmpty(logoPath) && string.IsNullOrEmpty(fallbackLogoPath))
            {
                var logoElement = doc.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "Logo" && 
                                         e.Parent?.Name.LocalName == "Properties");
                logoPath = logoElement?.Value;
            }

            // Try logos in order of preference
            var logoPaths = new[] { logoPath, fallbackLogoPath }
                .Where(p => !string.IsNullOrEmpty(p))
                .ToArray();

            if (logoPaths.Length == 0)
                return null;

            foreach (var currentLogoPath in logoPaths)
            {
                var baseLogoPath = Path.Combine(appDir, currentLogoPath!);
                var logoDir = Path.GetDirectoryName(baseLogoPath);
                var logoName = Path.GetFileNameWithoutExtension(baseLogoPath);
                var logoExt = Path.GetExtension(baseLogoPath);

                if (!string.IsNullOrEmpty(logoDir))
                {
                    // Try targetsize variants first (prefer larger sizes) - these are optimized for icon display
                    var sizes = new[] { ".targetsize-256", ".targetsize-128", ".targetsize-96", ".targetsize-64", 
                                        ".targetsize-48", ".targetsize-32" };
                    foreach (var size in sizes)
                    {
                        // Try with _altform-unplated suffix first (cleaner icons without background)
                        var unplatedPath = Path.Combine(logoDir, $"{logoName}{size}_altform-unplated{logoExt}");
                        var icon = LoadIconFromImageFile(unplatedPath);
                        if (icon != null)
                            return icon;

                        var sizePath = Path.Combine(logoDir, $"{logoName}{size}{logoExt}");
                        icon = LoadIconFromImageFile(sizePath);
                        if (icon != null)
                            return icon;
                    }

                    // Try scale variants
                    var scales = new[] { ".scale-400", ".scale-200", ".scale-150", ".scale-125", ".scale-100", "" };
                    foreach (var scale in scales)
                    {
                        var scaledPath = Path.Combine(logoDir, $"{logoName}{scale}{logoExt}");
                        var icon = LoadIconFromImageFile(scaledPath);
                        if (icon != null)
                            return icon;
                    }
                }

                // Try the exact path
                var exactIcon = LoadIconFromImageFile(baseLogoPath);
                if (exactIcon != null)
                    return exactIcon;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private ImageSource? LoadUwpIcon()
    {
        try
        {
            // Get the actual UWP process ID
            var uwpProcessId = GetUwpChildProcessId();
            
            // Get executable path
            var exePath = GetProcessPath(uwpProcessId);
            if (!string.IsNullOrEmpty(exePath))
            {
                // Try to load icon from AppxManifest.xml
                var manifestIcon = LoadIconFromAppxManifest(exePath);
                if (manifestIcon != null)
                    return manifestIcon;

                // Try to get icon from the executable using Shell API
                var icon = LoadIconFromShell(exePath);
                if (icon != null)
                    return icon;

                // Fallback: try ExtractAssociatedIcon
                try
                {
                    using var extractedIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                    if (extractedIcon != null)
                    {
                        return Imaging.CreateBitmapSourceFromHIcon(
                            extractedIcon.Handle,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                    }
                }
                catch
                {
                    // Ignore
                }
            }
        }
        catch
        {
            // Ignore errors
        }
        return null;
    }

    private ImageSource? LoadIcon()
    {
        try
        {
            // For UWP apps, try specialized method first
            if (IsUwpWindow)
            {
                var uwpIcon = LoadUwpIcon();
                if (uwpIcon != null)
                    return uwpIcon;
            }

            var iconHandle = GetWindowIconHandle();
            if (iconHandle != IntPtr.Zero)
            {
                var icon = LoadIconFromHandle(iconHandle);
                if (icon != null)
                    return icon;
            }

            return LoadIconFromProcess();
        }
        catch
        {
            return null;
        }
    }

    public void Activate()
    {
        // Get thread IDs
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        var foregroundThreadId = NativeMethods.GetWindowThreadProcessId(foregroundWindow, out uint foregroundProcessId);
        var currentThreadId = NativeMethods.GetCurrentThreadId();
        var targetThreadId = NativeMethods.GetWindowThreadProcessId(Handle, out uint targetProcessId);

        // Method 1: Temporarily disable foreground lock timeout
        uint oldTimeout = 0;
        bool timeoutModified = NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETFOREGROUNDLOCKTIMEOUT, 0, ref oldTimeout, 0);
        if (timeoutModified)
        {
            NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, NativeMethods.SPIF_SENDCHANGE);
        }

        // Method 2: Allow any process to set foreground window
        NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
        
        // Method 3: Unlock foreground window setting
        NativeMethods.LockSetForegroundWindow(NativeMethods.LSFW_UNLOCK);

        // Method 4: Attach to both foreground and target threads
        bool attachedToForeground = false;
        bool attachedToTarget = false;

        try
        {
            if (foregroundThreadId != currentThreadId && foregroundThreadId != 0)
            {
                attachedToForeground = NativeMethods.AttachThreadInput(currentThreadId, foregroundThreadId, true);
            }

            if (targetThreadId != currentThreadId && targetThreadId != foregroundThreadId && targetThreadId != 0)
            {
                attachedToTarget = NativeMethods.AttachThreadInput(currentThreadId, targetThreadId, true);
            }

            // Check current window state using GetWindowPlacement for accuracy
            var placement = new NativeMethods.WINDOWPLACEMENT();
            placement.length = System.Runtime.InteropServices.Marshal.SizeOf(placement);
            NativeMethods.GetWindowPlacement(Handle, ref placement);
            
            bool wasMaximized = placement.showCmd == NativeMethods.SW_SHOWMAXIMIZED_PLACEMENT || 
                                NativeMethods.IsZoomed(Handle) || 
                                IsMaximized;
            bool wasMinimized = placement.showCmd == NativeMethods.SW_SHOWMINIMIZED || 
                                NativeMethods.IsIconic(Handle) || 
                                IsMinimized;

            // Restore/show window appropriately
            if (wasMinimized)
            {
                // If it was minimized, restore to previous state (maximized or normal)
                if (wasMaximized)
                {
                    NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWMAXIMIZED);
                }
                else
                {
                    NativeMethods.ShowWindow(Handle, NativeMethods.SW_RESTORE);
                }
            }
            else if (wasMaximized)
            {
                // Already maximized, just show it
                NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWMAXIMIZED);
            }
            // For normal windows, don't call ShowWindow at all to preserve state

            // Try multiple activation methods
            NativeMethods.BringWindowToTop(Handle);
            NativeMethods.SetForegroundWindow(Handle);

            // If still not foreground, try more aggressive methods
            if (NativeMethods.GetForegroundWindow() != Handle)
            {
                // Simulate Alt key press to allow SetForegroundWindow
                NativeMethods.keybd_event(NativeMethods.VK_ALT, 0, NativeMethods.KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
                NativeMethods.keybd_event(NativeMethods.VK_ALT, 0, NativeMethods.KEYEVENTF_EXTENDEDKEY | NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);

                NativeMethods.SetForegroundWindow(Handle);
                NativeMethods.BringWindowToTop(Handle);
            }

            // Last resort: SwitchToThisWindow
            if (NativeMethods.GetForegroundWindow() != Handle)
            {
                NativeMethods.SwitchToThisWindow(Handle, true);
            }

            // Final attempt: SetWindowPos to bring to top
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
            // Activation failed, try fallback
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
            // Always detach threads if attached
            if (attachedToForeground)
            {
                NativeMethods.AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }
            if (attachedToTarget)
            {
                NativeMethods.AttachThreadInput(currentThreadId, targetThreadId, false);
            }

            // Always restore foreground lock timeout if we modified it
            if (timeoutModified)
            {
                NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETFOREGROUNDLOCKTIMEOUT, 0, ref oldTimeout, NativeMethods.SPIF_SENDCHANGE);
            }
        }
    }

    public bool MatchesFilter(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        var lowerFilter = filter.ToLowerInvariant();
        
        // Normal text matching
        if (Title.ToLowerInvariant().Contains(lowerFilter) ||
            ProcessName.ToLowerInvariant().Contains(lowerFilter))
            return true;

        // Pinyin matching (if enabled)
        if (Services.SettingsService.Instance.Settings.EnablePinyinSearch)
        {
            if (Services.PinyinService.Instance.MatchesPinyin(Title, filter) ||
                Services.PinyinService.Instance.MatchesPinyin(ProcessName, filter))
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

