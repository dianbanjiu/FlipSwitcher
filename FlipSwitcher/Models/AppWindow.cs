using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

    private ImageSource? LoadIcon()
    {
        try
        {
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

