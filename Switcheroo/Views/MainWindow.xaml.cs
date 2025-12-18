using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Switcheroo.Core;
using Switcheroo.Services;
using Switcheroo.ViewModels;

namespace Switcheroo.Views;

/// <summary>
/// Main window with Fluent 2 design for window switching
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly HotkeyService _hotkeyService;
    private bool _isClosing;
    private bool _isAltTabMode; // Track if we're in Alt+Tab hold mode
    private bool _isSearchMode; // Track if we're in search mode

    public HotkeyService HotkeyService => _hotkeyService;

    public MainWindow()
    {
        InitializeComponent();
        
        _viewModel = (MainViewModel)DataContext;
        _viewModel.WindowActivated += ViewModel_WindowActivated;

        _hotkeyService = new HotkeyService();
        _hotkeyService.HotkeyPressed += HotkeyService_HotkeyPressed;
        _hotkeyService.NavigationRequested += HotkeyService_NavigationRequested;
        _hotkeyService.AltReleased += HotkeyService_AltReleased;
        _hotkeyService.CloseWindowRequested += HotkeyService_CloseWindowRequested;
        _hotkeyService.SearchModeRequested += HotkeyService_SearchModeRequested;
        _hotkeyService.EscapePressed += HotkeyService_EscapePressed;

        // Listen for settings changes
        SettingsService.Instance.SettingsChanged += OnSettingsChanged;

        // Apply Mica/Acrylic effect on Windows 11
        Loaded += (s, e) => ApplyWindowEffects();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Register global hotkeys based on settings
        var settings = SettingsService.Instance.Settings;
        _hotkeyService.RegisterHotkeys(this, settings.UseAltSpace, settings.UseAltTab);
        
        // Update hotkey display
        UpdateHotkeyDisplay();

        // Initially hide the window
        Hide();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        // Re-register hotkeys when settings change
        var settings = SettingsService.Instance.Settings;
        _hotkeyService.RegisterHotkeys(this, settings.UseAltSpace, settings.UseAltTab);
        
        // Update hotkey display
        UpdateHotkeyDisplay();
    }

    private void UpdateHotkeyDisplay()
    {
        HotkeyDisplayText.Text = _hotkeyService.CurrentHotkey;
    }

    private void ApplyWindowEffects()
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        // Try to enable Mica effect (Windows 11)
        try
        {
            // Enable dark mode for title bar
            int darkMode = 1;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            // Try Mica backdrop (Windows 11 22H2+)
            int backdropType = 2; // DWMSBT_MAINWINDOW (Mica)
            var result = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));

            if (result != 0)
            {
                // Fallback: Try older Mica attribute
                int micaEffect = 1;
                NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_MICA_EFFECT, ref micaEffect, sizeof(int));
            }

            // Extend frame into client area for seamless effect
            var margins = new NativeMethods.MARGINS
            {
                cxLeftWidth = -1,
                cxRightWidth = -1,
                cyTopHeight = -1,
                cyBottomHeight = -1
            };
            NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);
        }
        catch
        {
            // Fallback to solid background on older Windows versions
        }
    }

    private void HotkeyService_HotkeyPressed(object? sender, EventArgs e)
    {
        if (IsVisible)
        {
            HideWindow();
        }
        else
        {
            // Check if this is Alt+Tab mode
            _isAltTabMode = _hotkeyService.IsAltTabEnabled;
            ShowWindow();
        }
    }

    private void HotkeyService_NavigationRequested(object? sender, NavigationEventArgs e)
    {
        if (!IsVisible) return;

        if (e.Direction == NavigationDirection.Next)
        {
            _viewModel.MoveSelectionDown();
        }
        else
        {
            _viewModel.MoveSelectionUp();
        }
        
        ScrollSelectedIntoView();
    }

    private void HotkeyService_AltReleased(object? sender, EventArgs e)
    {
        if (!IsVisible) return;

        // When Alt is released in Alt+Tab mode, activate the selected window
        if (_isAltTabMode)
        {
            _viewModel.ActivateSelected();
        }
    }

    private void HotkeyService_CloseWindowRequested(object? sender, EventArgs e)
    {
        if (!IsVisible) return;

        _viewModel.CloseSelectedWindow();
    }

    private void HotkeyService_SearchModeRequested(object? sender, EventArgs e)
    {
        if (!IsVisible) return;

        EnterSearchMode();
    }

    private void EnterSearchMode()
    {
        // Exit Alt+Tab mode to allow normal typing
        _isAltTabMode = false;
        _isSearchMode = true;
        
        // Tell hotkey service we're in search mode (don't intercept arrow keys)
        _hotkeyService.SetSearchMode(true);
        
        // Force window to foreground and focus
        ForceActivateWindow();
        
        // Focus the search box and allow typing
        // Use Dispatcher to ensure focus happens after any pending operations
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            ForceActivateWindow();
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            SearchBox.SelectAll();
        }));
    }

    private void ForceActivateWindow()
    {
        // Use the same techniques as AppWindow.Activate to force focus
        var hwnd = new WindowInteropHelper(this).Handle;
        
        // Simulate Alt key to allow SetForegroundWindow
        NativeMethods.keybd_event(NativeMethods.VK_ALT, 0, NativeMethods.KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_ALT, 0, NativeMethods.KEYEVENTF_EXTENDEDKEY | NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
        
        NativeMethods.SetForegroundWindow(hwnd);
        NativeMethods.BringWindowToTop(hwnd);
        
        Activate();
        Focus();
    }

    private void HotkeyService_EscapePressed(object? sender, EventArgs e)
    {
        // Always hide window when Escape is pressed, regardless of any state
        HideWindow();
    }

    private void ShowWindow()
    {
        // Refresh window list - in Alt+Tab mode, select the second window
        // (the first window is the current one, user wants to switch to another)
        _viewModel.RefreshWindows(selectSecondWindow: _isAltTabMode);
        _viewModel.ClearSearch();

        // Position window at center of primary screen
        var workArea = SystemParameters.WorkArea;
        Left = (workArea.Width - Width) / 2 + workArea.Left;
        Top = (workArea.Height - Height) / 2 + workArea.Top;

        Show();
        Activate();

        // Notify hotkey service that we're visible
        _hotkeyService.SetSwitcherooVisible(true);

        // Focus the search box (but don't select all in Alt+Tab mode)
        SearchBox.Focus();
        if (!_isAltTabMode)
        {
            SearchBox.SelectAll();
        }

        // Scroll selected window into view
        ScrollSelectedIntoView();
    }

    private void HideWindow()
    {
        if (!_isClosing)
        {
            Hide();
            _viewModel.ClearSearch();
            _isAltTabMode = false;
            _isSearchMode = false;
            
            // Notify hotkey service that we're hidden
            _hotkeyService.SetSwitcherooVisible(false);
            _hotkeyService.SetSearchMode(false);
        }
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        // Don't hide if in Alt+Tab mode (user might be holding Alt)
        if (_isAltTabMode)
        {
            return;
        }
        
        // Check if HideOnFocusLost setting is enabled
        if (!SettingsService.Instance.Settings.HideOnFocusLost)
        {
            // In search mode with HideOnFocusLost disabled, try to regain focus
            if (_isSearchMode)
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
                {
                    if (_isSearchMode && IsVisible)
                    {
                        ForceActivateWindow();
                        SearchBox.Focus();
                        Keyboard.Focus(SearchBox);
                    }
                }));
            }
            return;
        }
        
        HideWindow();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                HideWindow();
                e.Handled = true;
                break;

            case Key.Enter:
                _viewModel.ActivateSelected();
                e.Handled = true;
                break;

            case Key.Up:
                _viewModel.MoveSelectionUp();
                ScrollSelectedIntoView();
                e.Handled = true;
                break;

            case Key.Down:
                _viewModel.MoveSelectionDown();
                ScrollSelectedIntoView();
                e.Handled = true;
                break;

            case Key.Tab:
                if (Keyboard.Modifiers == ModifierKeys.Shift)
                    _viewModel.MoveSelectionUp();
                else
                    _viewModel.MoveSelectionDown();
                ScrollSelectedIntoView();
                e.Handled = true;
                break;

            case Key.W:
                // Alt+W to close selected window
                if (Keyboard.Modifiers == ModifierKeys.Alt)
                {
                    _viewModel.CloseSelectedWindow();
                    e.Handled = true;
                }
                break;

            case Key.S:
                // Alt+S to enter search mode
                if (Keyboard.Modifiers == ModifierKeys.Alt)
                {
                    EnterSearchMode();
                    e.Handled = true;
                }
                break;
        }
    }

    private void ScrollSelectedIntoView()
    {
        if (_viewModel.SelectedWindow != null)
        {
            WindowList.ScrollIntoView(_viewModel.SelectedWindow);
        }
    }

    private void WindowList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        _viewModel.ActivateSelected();
    }

    private void ViewModel_WindowActivated(object? sender, EventArgs e)
    {
        HideWindow();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Prevent closing, just hide instead
        if (!_isClosing)
        {
            e.Cancel = true;
            HideWindow();
        }
        else
        {
            _hotkeyService.Dispose();
            base.OnClosing(e);
        }
    }

    public void ForceClose()
    {
        _isClosing = true;
        Close();
    }
}
