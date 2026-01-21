using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using FlipSwitcher.Core;
using FlipSwitcher.Services;
using FlipSwitcher.ViewModels;

namespace FlipSwitcher.Views;

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
        _hotkeyService.StopProcessRequested += HotkeyService_StopProcessRequested;
        _hotkeyService.SearchModeRequested += HotkeyService_SearchModeRequested;
        _hotkeyService.EscapePressed += HotkeyService_EscapePressed;
        _hotkeyService.SettingsRequested += HotkeyService_SettingsRequested;
        _hotkeyService.GroupByProcessRequested += HotkeyService_GroupByProcessRequested;
        _hotkeyService.UngroupFromProcessRequested += HotkeyService_UngroupFromProcessRequested;

        // Listen for settings changes
        SettingsService.Instance.SettingsChanged += OnSettingsChanged;

        // Apply theme on Windows 11
        Loaded += (s, e) => ApplyWindowEffects();

        // Prevent double-click maximize
        MouseDoubleClick += (s, e) => e.Handled = true;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Register global hotkeys based on settings
        var settings = SettingsService.Instance.Settings;
        _hotkeyService.RegisterHotkeys(this, settings.UseAltSpace, settings.UseAltTab);
        
        // Update hotkey display
        UpdateHotkeyDisplay();

        // Intercept window messages to prevent double-click maximize
        var hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        if (hwndSource != null)
        {
            hwndSource.AddHook(WndProc);
        }

        // Initially hide the window
        Hide();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Block double-click on title bar to prevent maximize
        if (msg == NativeMethods.WM_NCLBUTTONDBLCLK)
        {
            handled = true;
            return IntPtr.Zero;
        }

        // Block SC_MAXIMIZE system command
        if (msg == NativeMethods.WM_SYSCOMMAND && wParam.ToInt32() == NativeMethods.SC_MAXIMIZE)
        {
            handled = true;
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        // Re-register hotkeys when settings change
        var settings = SettingsService.Instance.Settings;
        _hotkeyService.RegisterHotkeys(this, settings.UseAltSpace, settings.UseAltTab);
        
        // Update hotkey display
        UpdateHotkeyDisplay();
    }

    private const int ThemeDark = 0;
    private const int ThemeLight = 1;
    private const int DarkModeEnabled = 1;
    private const int DarkModeDisabled = 0;

    private void UpdateHotkeyDisplay()
    {
        HotkeyDisplayText.Text = _hotkeyService.CurrentHotkey;
    }

    private void ApplyWindowEffects()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var settings = SettingsService.Instance.Settings;

        try
        {
            bool isDark = settings.Theme switch
            {
                ThemeDark => true,
                ThemeLight => false,
                _ => true
            };
            int darkMode = isDark ? DarkModeEnabled : DarkModeDisabled;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
        }
        catch
        {
            // Fallback on older Windows versions
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
            _viewModel.MoveSelectionDown();
        else
            _viewModel.MoveSelectionUp();
        
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

        TryCloseSelectedWindow();
    }

    private void TryCloseSelectedWindow()
    {
        if (!_viewModel.CloseSelectedWindow())
        {
            // Cannot close elevated window, show prompt
            FluentDialog.Show(
                LanguageService.GetString("MsgCannotCloseElevatedWindow"),
                LanguageService.GetString("AppTitle"),
                FluentDialogButton.OK,
                FluentDialogIcon.Warning,
                this);
        }
    }

    private void HotkeyService_StopProcessRequested(object? sender, EventArgs e)
    {
        if (!IsVisible) return;

        _viewModel.StopSelectedProcess();
    }

    private void HotkeyService_SearchModeRequested(object? sender, EventArgs e)
    {
        if (!IsVisible) return;

        EnterSearchMode();
    }

    private void EnterSearchMode()
    {
        _isAltTabMode = false;
        _isSearchMode = true;
        _hotkeyService.SetSearchMode(true);
        
        ForceActivateWindow();
        
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

    private void HotkeyService_SettingsRequested(object? sender, EventArgs e)
    {
        // Hide main window and open settings
        HideWindow();
        _hotkeyService.SetSettingsWindowOpen(true);
        var settingsWindow = new SettingsWindow(_hotkeyService);
        settingsWindow.Owner = this;
        settingsWindow.Closed += (s, args) => _hotkeyService.SetSettingsWindowOpen(false);
        settingsWindow.ShowDialog();
    }

    private void HotkeyService_GroupByProcessRequested(object? sender, EventArgs e)
    {
        if (!IsVisible) return;
        _viewModel.GroupByProcess();
        ScrollSelectedIntoView();
    }

    private void HotkeyService_UngroupFromProcessRequested(object? sender, EventArgs e)
    {
        if (!IsVisible) return;
        _viewModel.UngroupFromProcess();
        ScrollSelectedIntoView();
    }

    private void ShowWindow()
    {
        // 重置分组状态，确保显示总列表
        _viewModel.ResetGrouping();
        
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
        _hotkeyService.SetVisible(true);

        // Focus the search box (but don't select all in Alt+Tab mode)
        SearchBox.Focus();
        if (!_isAltTabMode)
        {
            SearchBox.SelectAll();
        }

        // Delay scroll execution to ensure layout is complete
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            ScrollToTopThenSelected();
        }));
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
            _hotkeyService.SetVisible(false);
            _hotkeyService.SetSearchMode(false);
        }
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        // Don't hide if in Alt+Tab mode AND Alt key is still pressed (user might be holding Alt)
        if (_isAltTabMode)
        {
            // Check if Alt is still pressed - if not, we should hide
            if (IsAltKeyPressed())
            {
                return;
            }
            // Alt is no longer pressed, exit Alt+Tab mode
            _isAltTabMode = false;
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

    /// <summary>
    /// Check if any Alt key is currently pressed
    /// </summary>
    private bool IsAltKeyPressed()
    {
        return (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0 ||
               (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LMENU) & 0x8000) != 0 ||
               (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RMENU) & 0x8000) != 0;
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

            case Key.Right:
                // When search box focused: Alt+Right groups, plain Right moves cursor
                if (SearchBox.IsFocused && Keyboard.Modifiers != ModifierKeys.Alt)
                    break;
                _viewModel.GroupByProcess();
                ScrollSelectedIntoView();
                e.Handled = true;
                break;

            case Key.Left:
                // When search box focused: Alt+Left ungroups, plain Left moves cursor
                if (SearchBox.IsFocused && Keyboard.Modifiers != ModifierKeys.Alt)
                    break;
                _viewModel.UngroupFromProcess();
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
                    TryCloseSelectedWindow();
                    e.Handled = true;
                }
                break;

            case Key.D:
                // Alt+D to stop selected process
                if (Keyboard.Modifiers == ModifierKeys.Alt)
                {
                    _viewModel.StopSelectedProcess();
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

            case Key.OemComma:
                // Alt+, to open settings
                if (Keyboard.Modifiers == ModifierKeys.Alt)
                {
                    HotkeyService_SettingsRequested(this, EventArgs.Empty);
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

    /// <summary>
    /// Force scroll to top of list (used during initialization)
    /// </summary>
    private void ScrollToTopThenSelected()
    {
        if (_viewModel.FilteredWindows.Count == 0) return;
        
        // Get the internal ScrollViewer and force scroll to top
        var scrollViewer = GetScrollViewer(WindowList);
        if (scrollViewer != null)
        {
            scrollViewer.ScrollToTop();
        }
        else
        {
            // Fallback: scroll to first item
            WindowList.ScrollIntoView(_viewModel.FilteredWindows[0]);
        }
    }

    /// <summary>
    /// Get the internal ScrollViewer of a ListBox
    /// </summary>
    private static System.Windows.Controls.ScrollViewer? GetScrollViewer(System.Windows.DependencyObject element)
    {
        if (element is System.Windows.Controls.ScrollViewer sv)
            return sv;

        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(element, i);
            var result = GetScrollViewer(child);
            if (result != null)
                return result;
        }
        return null;
    }

    private void WindowList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 单击时直接激活选中的窗口
        if (_viewModel.SelectedWindow != null)
        {
            _viewModel.ActivateSelected();
        }
    }

    private void ViewModel_WindowActivated(object? sender, EventArgs e)
    {
        // 重置分组状态，确保下次打开时显示总列表
        _viewModel.ResetGrouping();
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
