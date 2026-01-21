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
    private bool? _isElevated;

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
            _isElevated ??= CheckProcessElevation();
            return _isElevated.Value;
        }
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

    private const uint IconTimeoutMs = 50;

    private bool IsUwpWindow => ClassName == "ApplicationFrameWindow";

    // 统一的图标句柄转 ImageSource
    private static ImageSource? IconHandleToImageSource(IntPtr iconHandle, bool destroyAfter = false)
    {
        if (iconHandle == IntPtr.Zero) return null;
        try
        {
            using var icon = System.Drawing.Icon.FromHandle(iconHandle);
            using var cloned = (System.Drawing.Icon)icon.Clone();
            return Imaging.CreateBitmapSourceFromHIcon(cloned.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        catch { return null; }
        finally { if (destroyAfter) NativeMethods.DestroyIcon(iconHandle); }
    }

    // 从窗口消息获取图标句柄
    private IntPtr GetWindowIconHandle()
    {
        // 优先 ICON_BIG，跳过 ICON_SMALL2（较少使用）
        NativeMethods.SendMessageTimeout(Handle, NativeMethods.WM_GETICON, (IntPtr)NativeMethods.ICON_BIG, IntPtr.Zero,
            NativeMethods.SMTO_ABORTIFHUNG, IconTimeoutMs, out var h);
        if (h != IntPtr.Zero) return h;

        NativeMethods.SendMessageTimeout(Handle, NativeMethods.WM_GETICON, (IntPtr)NativeMethods.ICON_SMALL, IntPtr.Zero,
            NativeMethods.SMTO_ABORTIFHUNG, IconTimeoutMs, out h);
        if (h != IntPtr.Zero) return h;

        h = NativeMethods.GetClassLongPtr(Handle, NativeMethods.GCL_HICON);
        return h != IntPtr.Zero ? h : NativeMethods.GetClassLongPtr(Handle, NativeMethods.GCL_HICONSM);
    }

    // 获取进程路径
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

    // Shell API 获取图标（适用于所有应用包括 UWP）
    private ImageSource? LoadIconFromShell(string filePath)
    {
        var shinfo = new NativeMethods.SHFILEINFO();
        var result = NativeMethods.SHGetFileInfo(filePath, 0, ref shinfo,
            (uint)System.Runtime.InteropServices.Marshal.SizeOf(shinfo),
            NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON);
        return result != 0 ? IconHandleToImageSource(shinfo.hIcon, destroyAfter: true) : null;
    }

    // 从图片文件加载（UWP manifest 资源）
    private ImageSource? LoadIconFromImageFile(string imagePath)
    {
        if (!File.Exists(imagePath)) return null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 48; // 限制解码尺寸提升性能
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    // UWP: 从 AppxManifest 获取图标
    private ImageSource? LoadIconFromAppxManifest(string appDir)
    {
        var manifestPath = Path.Combine(appDir, "AppxManifest.xml");
        if (!File.Exists(manifestPath)) return null;

        try
        {
            var doc = XDocument.Load(manifestPath);
            var visualElements = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "VisualElements");
            
            // 尝试多个 logo 属性
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

            // 尝试常用变体（优先大尺寸和 unplated）
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

    // UWP 图标加载
    private ImageSource? LoadUwpIcon()
    {
        // 获取真实 UWP 进程 ID（尝试多种子窗口类型）
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
        
        // 优先 Manifest（更准确）
        if (!string.IsNullOrEmpty(appDir))
        {
            var icon = LoadIconFromAppxManifest(appDir);
            if (icon != null) return icon;
        }

        // 备选：Shell API
        return LoadIconFromShell(exePath);
    }

    private ImageSource? LoadIcon()
    {
        // UWP 应用走专用路径
        if (IsUwpWindow)
        {
            var uwpIcon = LoadUwpIcon();
            if (uwpIcon != null) return uwpIcon;
        }

        // 1. 窗口图标句柄（最快）
        var iconHandle = GetWindowIconHandle();
        if (iconHandle != IntPtr.Zero)
        {
            var icon = IconHandleToImageSource(iconHandle);
            if (icon != null) return icon;
        }

        // 2. Shell API（可靠）
        var exePath = GetProcessPath(ProcessId);
        if (!string.IsNullOrEmpty(exePath))
        {
            var icon = LoadIconFromShell(exePath);
            if (icon != null) return icon;
        }

        // 3. 从进程模块提取（兜底）
        try
        {
            using var process = Process.GetProcessById((int)ProcessId);
            if (process.MainModule?.FileName != null)
            {
                using var ico = System.Drawing.Icon.ExtractAssociatedIcon(process.MainModule.FileName);
                if (ico != null) return IconHandleToImageSource(ico.Handle);
            }
        }
        catch { }

        return null;
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

        if (Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            return true;

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

