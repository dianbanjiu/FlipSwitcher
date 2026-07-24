using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
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
    private bool _hookInstalled;
    private bool _altSpaceRegistered;

    // Low-level keyboard hook for Alt+Tab
    private IntPtr _keyboardHookId = IntPtr.Zero;
    private NativeMethods.LowLevelKeyboardProc? _keyboardProc;
    private readonly object _hookLock = new();
    private Thread? _keyboardHookThread;
    private uint _keyboardHookThreadId;
    private volatile bool _useAltTab;
    private volatile bool _isVisible;
    private volatile bool _isSearchMode;
    private volatile bool _isSettingsWindowOpen;
    private volatile bool _hookStopRequested;
    private int _modifierState;

    private const int ModifierAlt = 0x07;
    private const int ModifierShift = 0x38;

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
    internal bool IsKeyboardHookRunning
    {
        get
        {
            lock (_hookLock)
            {
                return _keyboardHookId != IntPtr.Zero &&
                       _keyboardHookThread?.IsAlive == true;
            }
        }
    }
    internal uint KeyboardHookThreadId
    {
        get
        {
            lock (_hookLock)
            {
                return _keyboardHookThreadId;
            }
        }
    }

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

        if (!_hookInstalled)
        {
            _source = HwndSource.FromHwnd(_windowHandle);
            _source?.AddHook(WndProc);
            _hookInstalled = true;
        }

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
        var hookReady = new ManualResetEventSlim(false);

        lock (_hookLock)
        {
            if (_keyboardHookThread?.IsAlive == true)
            {
                hookReady.Dispose();
                return _keyboardHookId != IntPtr.Zero;
            }

            _keyboardHookThread = new Thread(() => KeyboardHookThreadMain(hookReady))
            {
                IsBackground = true,
                Name = "FlipSwitcher keyboard hook"
            };
            _hookStopRequested = false;
            _keyboardHookThread.Start();
        }

        if (!hookReady.Wait(TimeSpan.FromSeconds(2)))
        {
            UninstallKeyboardHook();
            return false;
        }

        hookReady.Dispose();
        lock (_hookLock)
        {
            return _keyboardHookId != IntPtr.Zero;
        }
    }

    private void KeyboardHookThreadMain(ManualResetEventSlim hookReady)
    {
        IntPtr hookId = IntPtr.Zero;
        bool readySignaled = false;
        try
        {
            uint threadId = NativeMethods.GetCurrentThreadId();

            // Force creation of this thread's message queue before another thread can post WM_QUIT.
            NativeMethods.PeekMessage(
                out _,
                IntPtr.Zero,
                0,
                0,
                NativeMethods.PM_NOREMOVE);

            NativeMethods.LowLevelKeyboardProc keyboardProc = KeyboardHookCallback;
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            if (curModule != null)
            {
                hookId = NativeMethods.SetWindowsHookEx(
                    NativeMethods.WH_KEYBOARD_LL,
                    keyboardProc,
                    NativeMethods.GetModuleHandle(curModule.ModuleName),
                    0);
            }

            lock (_hookLock)
            {
                _keyboardProc = hookId != IntPtr.Zero ? keyboardProc : null;
                _keyboardHookId = hookId;
                _keyboardHookThreadId = threadId;
            }

            hookReady.Set();
            readySignaled = true;
            if (hookId == IntPtr.Zero)
                return;
            if (_hookStopRequested)
                return;

            int messageResult;
            do
            {
                messageResult = NativeMethods.GetMessage(
                    out _,
                    IntPtr.Zero,
                    0,
                    0);
            }
            while (messageResult > 0);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Keyboard hook thread failed: {ex}");
            if (!readySignaled)
                hookReady.Set();
        }
        finally
        {
            if (hookId != IntPtr.Zero)
                NativeMethods.UnhookWindowsHookEx(hookId);

            Volatile.Write(ref _modifierState, 0);
            lock (_hookLock)
            {
                if (_keyboardHookThread == Thread.CurrentThread)
                {
                    _keyboardHookId = IntPtr.Zero;
                    _keyboardHookThreadId = 0;
                    _keyboardProc = null;
                    _keyboardHookThread = null;
                }
            }
        }
    }

    private void UninstallKeyboardHook()
    {
        Thread? hookThread;
        uint hookThreadId;
        IntPtr hookId;

        _hookStopRequested = true;
        lock (_hookLock)
        {
            hookThread = _keyboardHookThread;
            hookThreadId = _keyboardHookThreadId;
            hookId = _keyboardHookId;
        }

        if (hookThreadId != 0)
        {
            NativeMethods.PostThreadMessage(
                hookThreadId,
                NativeMethods.WM_QUIT,
                UIntPtr.Zero,
                IntPtr.Zero);
        }

        if (hookThread != null &&
            hookThread != Thread.CurrentThread &&
            hookThread.IsAlive &&
            !hookThread.Join(TimeSpan.FromSeconds(1)) &&
            hookId != IntPtr.Zero)
        {
            // The queue normally exits immediately. If it does not, at least remove the global
            // hook so a stalled shutdown cannot affect keyboard input in other applications.
            NativeMethods.UnhookWindowsHookEx(hookId);
        }

        Volatile.Write(ref _modifierState, 0);
    }

    private bool IsAltPressed()
    {
        return (Volatile.Read(ref _modifierState) & ModifierAlt) != 0;
    }

    private bool IsShiftPressed()
    {
        return (Volatile.Read(ref _modifierState) & ModifierShift) != 0;
    }

    private void UpdateModifierState(uint vkCode, bool isKeyDown, bool isKeyUp)
    {
        int modifier = vkCode switch
        {
            NativeMethods.VK_MENU => 0x01,
            NativeMethods.VK_LMENU => 0x02,
            NativeMethods.VK_RMENU => 0x04,
            NativeMethods.VK_SHIFT => 0x08,
            NativeMethods.VK_LSHIFT => 0x10,
            NativeMethods.VK_RSHIFT => 0x20,
            _ => 0
        };

        if (modifier == 0)
            return;

        if (isKeyDown)
            Interlocked.Or(ref _modifierState, modifier);
        else if (isKeyUp)
            Interlocked.And(ref _modifierState, ~modifier);
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
        if (!_isVisible)
            return false;

        // Up/Down arrows: navigate only when not in search mode
        if (!_isSearchMode)
        {
            switch (vkCode)
            {
                case NativeMethods.VK_UP:
                    InvokeOnDispatcher(() => NavigationRequested?.Invoke(this, new NavigationEventArgs(NavigationDirection.Previous)));
                    return true;
                case NativeMethods.VK_DOWN:
                    InvokeOnDispatcher(() => NavigationRequested?.Invoke(this, new NavigationEventArgs(NavigationDirection.Next)));
                    return true;
            }
        }

        // Left/Right arrows: always available (requires Alt in search mode)
        switch (vkCode)
        {
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
        // Hot path: this runs for every keystroke system-wide. Be paranoid about cost.
        if (nCode < 0 || !_useAltTab)
            return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

        long msg = wParam.ToInt64();
        bool isKeyDown = msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN;
        bool isKeyUp = msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP;

        // Neither down nor up — don't bother decoding the struct.
        if (!isKeyDown && !isKeyUp)
            return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

        // KBDLLHOOKSTRUCT layout: vkCode is the first field (UInt32 at offset 0).
        // Reading it directly via unsafe pointer avoids the cost of full Marshal.PtrToStructure
        // (allocation, field-by-field unmarshaling) on every keystroke. We never need the other
        // fields in this hook.
        uint vkCode;
        unsafe
        {
            vkCode = *(uint*)lParam;
        }

        UpdateModifierState(vkCode, isKeyDown, isKeyUp);

        if (HandleEscapeKey(isKeyDown, vkCode))
            return (IntPtr)1;

        if (isKeyUp)
        {
            HandleAltRelease(true, vkCode);
            return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        // From here on, isKeyDown == true. Only act when Alt is held.
        if (!IsAltPressed())
            return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

        // Tab key — show/navigate.
        if (vkCode == NativeMethods.VK_TAB)
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

        if (HandleNavigationKeys(vkCode) || HandleVisibleShortcuts(vkCode))
            return (IntPtr)1;

        return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
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
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt64() == HOTKEY_ID_ALT_SPACE)
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
