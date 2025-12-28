using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using FlipSwitcher.Core;

namespace FlipSwitcher.Services;

/// <summary>
/// Navigation direction for Alt+Tab mode
/// </summary>
public enum NavigationDirection
{
    Next,
    Previous
}

/// <summary>
/// Event args for navigation events
/// </summary>
public class NavigationEventArgs : EventArgs
{
    public NavigationDirection Direction { get; }
    
    public NavigationEventArgs(NavigationDirection direction)
    {
        Direction = direction;
    }
}

/// <summary>
/// Service for managing global hotkeys including Alt+Tab interception
/// </summary>
public class HotkeyService : IDisposable
{
    private const int HOTKEY_ID_ALT_SPACE = 9000;
    
    // Virtual key codes
    private const uint VK_SPACE = 0x20;
    
    private IntPtr _windowHandle;
    private HwndSource? _source;
    private bool _altSpaceRegistered;
    
    // Low-level keyboard hook for Alt+Tab
    private IntPtr _keyboardHookId = IntPtr.Zero;
    private NativeMethods.LowLevelKeyboardProc? _keyboardProc;
    private bool _useAltTab;
    private bool _isVisible;
    private bool _isSearchMode;
    private bool _isSettingsWindowOpen;

    /// <summary>
    /// Fired when the activation hotkey is pressed (to show/hide FlipSwitcher)
    /// </summary>
    public event EventHandler? HotkeyPressed;
    
    /// <summary>
    /// Fired when navigation keys are pressed while Alt is held (Tab, Shift+Tab, Up, Down)
    /// </summary>
    public event EventHandler<NavigationEventArgs>? NavigationRequested;
    
    /// <summary>
    /// Fired when Alt key is released (to confirm selection)
    /// </summary>
    public event EventHandler? AltReleased;

    /// <summary>
    /// Fired when Alt+W is pressed (to close selected window)
    /// </summary>
    public event EventHandler? CloseWindowRequested;

    /// <summary>
    /// Fired when Alt+D is pressed (to stop selected process)
    /// </summary>
    public event EventHandler? StopProcessRequested;

    /// <summary>
    /// Fired when Alt+S is pressed (to enter search mode)
    /// </summary>
    public event EventHandler? SearchModeRequested;

    /// <summary>
    /// Fired when Escape is pressed (to close window)
    /// </summary>
    public event EventHandler? EscapePressed;

    /// <summary>
    /// Fired when Alt+, is pressed (to open settings)
    /// </summary>
    public event EventHandler? SettingsRequested;

    /// <summary>
    /// Fired when Right arrow key is pressed (to group by process)
    /// </summary>
    public event EventHandler? GroupByProcessRequested;

    /// <summary>
    /// Fired when Left arrow key is pressed (to ungroup from process)
    /// </summary>
    public event EventHandler? UngroupFromProcessRequested;

    public string CurrentHotkey { get; private set; } = "Alt + Tab";
    public bool IsAltTabEnabled => _useAltTab;

    public HotkeyService()
    {
    }

    /// <summary>
    /// Update the visibility state of FlipSwitcher (for keyboard hook logic)
    /// </summary>
    public void SetVisible(bool visible)
    {
        _isVisible = visible;
        if (!visible)
        {
            _isSearchMode = false;
        }
    }

    /// <summary>
    /// Set search mode - when true, arrow keys are not intercepted by the hook
    /// </summary>
    public void SetSearchMode(bool searchMode)
    {
        _isSearchMode = searchMode;
    }

    /// <summary>
    /// Set settings window open state - when true, Alt+Esc will close settings window
    /// </summary>
    public void SetSettingsWindowOpen(bool isOpen)
    {
        _isSettingsWindowOpen = isOpen;
    }

    public void RegisterHotkeys(Window window, bool useAltSpace = false, bool useAltTab = true)
    {
        var helper = new WindowInteropHelper(window);
        _windowHandle = helper.EnsureHandle();

        _source = HwndSource.FromHwnd(_windowHandle);
        _source?.AddHook(WndProc);

        // Unregister existing hotkeys first
        UnregisterAllHotkeys();

        var registeredKeys = new List<string>();
        _useAltTab = useAltTab;

        // Register Alt + Space using RegisterHotKey
        if (useAltSpace)
        {
            if (NativeMethods.RegisterHotKey(_windowHandle, HOTKEY_ID_ALT_SPACE, 
                NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, VK_SPACE))
            {
                _altSpaceRegistered = true;
                registeredKeys.Add("Alt + Space");
            }
        }

        // Register Alt + Tab using low-level keyboard hook
        if (useAltTab)
        {
            if (InstallKeyboardHook())
            {
                registeredKeys.Add("Alt + Tab");
            }
        }

        // Fallback to Ctrl + Space if nothing registered
        if (registeredKeys.Count == 0)
        {
            if (NativeMethods.RegisterHotKey(_windowHandle, HOTKEY_ID_ALT_SPACE,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_NOREPEAT, VK_SPACE))
            {
                _altSpaceRegistered = true;
                registeredKeys.Add("Ctrl + Space");
            }
        }

        CurrentHotkey = string.Join(" / ", registeredKeys);
    }

    private bool InstallKeyboardHook()
    {
        if (_keyboardHookId != IntPtr.Zero)
            return true;

        _keyboardProc = KeyboardHookCallback;
        
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        
        if (curModule != null)
        {
            _keyboardHookId = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_KEYBOARD_LL,
                _keyboardProc,
                NativeMethods.GetModuleHandle(curModule.ModuleName),
                0);
        }

        return _keyboardHookId != IntPtr.Zero;
    }

    private void UninstallKeyboardHook()
    {
        if (_keyboardHookId != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHookId);
            _keyboardHookId = IntPtr.Zero;
        }
        _keyboardProc = null;
    }

    private bool IsAltPressed()
    {
        return (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0 ||
               (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LMENU) & 0x8000) != 0 ||
               (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RMENU) & 0x8000) != 0;
    }

    private bool IsShiftPressed()
    {
        return (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;
    }

    private void InvokeOnDispatcher(Action action)
    {
        Application.Current?.Dispatcher.BeginInvoke(action);
    }

    private bool HandleEscapeKey(bool isKeyDown, uint vkCode)
    {
        if (!isKeyDown || vkCode != NativeMethods.VK_ESCAPE)
            return false;

        if ((_isSettingsWindowOpen && IsAltPressed()) || _isVisible)
        {
            InvokeOnDispatcher(() => EscapePressed?.Invoke(this, EventArgs.Empty));
            return true;
        }
        return false;
    }

    private void HandleAltRelease(bool isKeyUp, uint vkCode)
    {
        if (!isKeyUp || !_isVisible)
            return;

        if (vkCode == NativeMethods.VK_MENU || 
            vkCode == NativeMethods.VK_LMENU || 
            vkCode == NativeMethods.VK_RMENU)
        {
            InvokeOnDispatcher(() => AltReleased?.Invoke(this, EventArgs.Empty));
        }
    }

    private bool HandleNavigationKeys(uint vkCode)
    {
        if (!_isVisible || _isSearchMode)
            return false;

        switch (vkCode)
        {
            case NativeMethods.VK_UP:
                InvokeOnDispatcher(() => NavigationRequested?.Invoke(this, new NavigationEventArgs(NavigationDirection.Previous)));
                return true;
            case NativeMethods.VK_DOWN:
                InvokeOnDispatcher(() => NavigationRequested?.Invoke(this, new NavigationEventArgs(NavigationDirection.Next)));
                return true;
            case NativeMethods.VK_RIGHT:
                InvokeOnDispatcher(() => GroupByProcessRequested?.Invoke(this, EventArgs.Empty));
                return true;
            case NativeMethods.VK_LEFT:
                InvokeOnDispatcher(() => UngroupFromProcessRequested?.Invoke(this, EventArgs.Empty));
                return true;
        }
        return false;
    }

    private bool HandleVisibleShortcuts(uint vkCode)
    {
        if (!_isVisible)
            return false;

        switch (vkCode)
        {
            case NativeMethods.VK_W:
                InvokeOnDispatcher(() => CloseWindowRequested?.Invoke(this, EventArgs.Empty));
                return true;
            case NativeMethods.VK_D:
                InvokeOnDispatcher(() => StopProcessRequested?.Invoke(this, EventArgs.Empty));
                return true;
            case NativeMethods.VK_S:
                InvokeOnDispatcher(() => SearchModeRequested?.Invoke(this, EventArgs.Empty));
                return true;
            case NativeMethods.VK_OEM_COMMA:
                InvokeOnDispatcher(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
                return true;
        }
        return false;
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _useAltTab)
        {
            var hookStruct = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            int msg = wParam.ToInt32();
            bool isKeyDown = msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN;
            bool isKeyUp = msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP;

            if (HandleEscapeKey(isKeyDown, hookStruct.vkCode))
                return (IntPtr)1;

            HandleAltRelease(isKeyUp, hookStruct.vkCode);

            if (IsAltPressed() && isKeyDown)
            {
                // Tab 键 - 显示/导航
                if (hookStruct.vkCode == NativeMethods.VK_TAB)
                {
                    if (!_isVisible)
                        InvokeOnDispatcher(() => HotkeyPressed?.Invoke(this, EventArgs.Empty));
                    else
                    {
                        var direction = IsShiftPressed() ? NavigationDirection.Previous : NavigationDirection.Next;
                        InvokeOnDispatcher(() => NavigationRequested?.Invoke(this, new NavigationEventArgs(direction)));
                    }
                    return (IntPtr)1;
                }

                if (HandleNavigationKeys(hookStruct.vkCode) || HandleVisibleShortcuts(hookStruct.vkCode))
                    return (IntPtr)1;
            }
        }

        return NativeMethods.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
    }

    public void UnregisterAllHotkeys()
    {
        if (_altSpaceRegistered && _windowHandle != IntPtr.Zero)
        {
            NativeMethods.UnregisterHotKey(_windowHandle, HOTKEY_ID_ALT_SPACE);
            _altSpaceRegistered = false;
        }

        UninstallKeyboardHook();
        _useAltTab = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID_ALT_SPACE)
        {
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAllHotkeys();
        _source?.RemoveHook(WndProc);
        _source?.Dispose();
    }
}
