using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FlipSwitcher.Controls;
using FlipSwitcher.Core;
using FlipSwitcher.Models;
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

    public static readonly DependencyProperty IsSearchModeProperty = DependencyProperty.Register(
        nameof(IsSearchMode),
        typeof(bool),
        typeof(MainWindow));

    public bool IsSearchMode
    {
        get => (bool)GetValue(IsSearchModeProperty);
        private set => SetValue(IsSearchModeProperty, value);
    }

    public HotkeyService HotkeyService => _hotkeyService;

    public MainWindow()
    {
        InitializeComponent();
        
        _viewModel = (MainViewModel)DataContext;
        _viewModel.WindowActivated += ViewModel_WindowActivated;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;

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
        if (msg == (int)NativeMethods.WM_NCLBUTTONDBLCLK)
        {
            handled = true;
            return IntPtr.Zero;
        }

        // Block SC_MAXIMIZE system command
        if (msg == (int)NativeMethods.WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == NativeMethods.SC_MAXIMIZE)
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
                Services.AppTheme.Light => false,
                Services.AppTheme.Latte => false,
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

        // Held-Alt+Tab cycling must reach every window in MRU order (wrap), in BOTH view
        // modes — spatial grid movement would strand the user in one column. Arrow keys
        // (Window_PreviewKeyDown) remain spatial in grid mode per spec §6.
        if (e.Direction == NavigationDirection.Next)
            _viewModel.MoveSelectionDown();
        else
            _viewModel.MoveSelectionUp();

        ScrollSelectedIntoView();
        ScrollGridSelectedIntoView();
    }

    private void HotkeyService_AltReleased(object? sender, EventArgs e)
    {
        if (!IsVisible) return;

        // When Alt is released in Alt+Tab mode, activate the selected window
        if (_isAltTabMode)
        {
            ActivateSelectedWindow();
        }
    }

    private void ActivateSelectedWindow()
    {
        var selectedWindow = _viewModel.SelectedWindow;
        if (selectedWindow == null)
        {
            HideWindow();
            return;
        }

        // Skip activation for elevated windows when not running as admin (UIPI) —
        // hide the popup and tell the user why instead of failing silently.
        if (selectedWindow.IsElevated && !AdminService.IsRunningAsAdmin())
        {
            HideWindow();
            FluentDialog.Show(
                LanguageService.GetString("MsgCannotSwitchToElevatedWindow"),
                LanguageService.GetString("AppTitle"),
                FluentDialogButton.OK,
                FluentDialogIcon.Warning,
                this);
            return;
        }

        // Ignore simulated Alt release events during window activation
        _hotkeyService.SetIgnoreAltRelease(true);
        _viewModel.ActivateSelected();
    }

    /// <summary>
    /// Activate a specific window from a grid card click (selects it first so Alt+W etc.
    /// keep operating on the clicked card).
    /// </summary>
    internal void ActivateCard(AppWindow window)
    {
        _viewModel.SelectedWindow = window;
        ActivateSelectedWindow();
    }

    private void HotkeyService_CloseWindowRequested(object? sender, EventArgs e)
    {
        if (!IsVisible) return;

        TryCloseSelectedWindow();
    }

    private void TryCloseSelectedWindow()
    {
        TryCloseWindow(_viewModel.SelectedWindow);
    }

    private void TryCloseWindow(AppWindow? window)
    {
        if (!_viewModel.CloseWindow(window))
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
        IsSearchMode = true;
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

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Live re-size while open: search and grouping change the window count, which
        // recomputes the grid layout.
        if (e.PropertyName == nameof(MainViewModel.Layout) && IsVisible)
            ApplyGridLayout();
    }

    /// <summary>
    /// Apply the grid's computed popup size and re-center. List mode restores the fixed
    /// 640x520 (spec §9 item 10), overriding any size left behind by a previous grid session.
    /// </summary>
    private void ApplyGridLayout()
    {
        if (!_viewModel.IsGridView)
        {
            // List mode is fixed-size (spec §9 item 10) — restore in case a previous
            // grid session resized the popup, and re-center so the first open after a
            // Grid→List switch isn't offset by the size delta.
            Width = 640;
            Height = 520;
            RecenterPopup();
            return;
        }

        var layout = _viewModel.Layout;
        Width = layout.PopupWidth;
        Height = layout.PopupHeight;
        RecenterPopup();
    }

    private void RecenterPopup()
    {
        if (SettingsService.Instance.Settings.ShowOnMouseScreen)
        {
            PositionOnMouseScreen();
        }
        else
        {
            var workArea = SystemParameters.WorkArea;
            Left = (workArea.Width - Width) / 2 + workArea.Left;
            Top = (workArea.Height - Height) / 2 + workArea.Top;
        }
    }

    private async void ShowWindow()
    {
        // Reset grouping state to ensure the full list is shown
        _viewModel.ResetGrouping();
        _viewModel.ClearSearch();

        var settings = SettingsService.Instance.Settings;
        bool openSearchDirectly = settings.OpenSearchOnActivation;

        // When opening search directly, don't use Alt+Tab hold mode
        if (openSearchDirectly)
            _isAltTabMode = false;

        // Position and show the window FIRST so it appears instantly. The (potentially slow)
        // window enumeration then runs on a background thread and populates the list a moment
        // later — it no longer blocks the window from painting. The list briefly shows the
        // previous session's contents until RefreshWindowsAsync completes (typically <1 frame).
        RecenterPopup();

        Show();
        Activate();

        // Notify hotkey service that we're visible
        _hotkeyService.SetVisible(true);

        if (openSearchDirectly)
        {
            EnterSearchMode();
        }
        else
        {
            // Focus the search box (but don't select all in Alt+Tab mode)
            SearchBox.Focus();
            if (!_isAltTabMode)
            {
                SearchBox.SelectAll();
            }
        }

        // Refresh window list off the UI thread - in Alt+Tab mode, select the second window
        // (the first window is the current one, user wants to switch to another). Selection
        // is applied after enumeration completes, before the user releases Alt in practice.
        await _viewModel.RefreshWindowsAsync(selectSecondWindow: _isAltTabMode);

        ApplyGridLayout();

        // Delay scroll execution to ensure layout is complete. Fire-and-forget: DispatcherOperation
        // is awaitable, so discard it to avoid CS4014 now that ShowWindow is async.
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            ScrollToTopThenSelected();
        }));
    }

    private void PositionOnMouseScreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        if (!NativeMethods.GetCursorPos(out var cursorPos)) return;

        var hMonitor = NativeMethods.MonitorFromPoint(cursorPos, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(hMonitor, ref monitorInfo)) return;

        double targetDpiScale = 1.0;
        if (NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out uint _) == 0)
            targetDpiScale = dpiX / 96.0;

        int expectedWidthPx = (int)(Width * targetDpiScale);
        int expectedHeightPx = (int)(Height * targetDpiScale);

        var rc = monitorInfo.rcWork;
        int x = rc.Left + (rc.Right - rc.Left - expectedWidthPx) / 2;
        int y = rc.Top + (rc.Bottom - rc.Top - expectedHeightPx) / 2;

        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
            x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    private void HideWindow()
    {
        if (!_isClosing)
        {
            Hide();
            _viewModel.ClearSearch();
            _isAltTabMode = false;
            IsSearchMode = false;

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
            if (IsSearchMode)
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
                {
                    if (IsSearchMode && IsVisible)
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
        // Alt + vim keys: H/L move left/right and J/K move down/up between cards in grid
        // view (mirroring the arrow keys); list view supports J/K only (mirroring its
        // arrows). While Alt is held WPF reports e.Key == Key.System with the real key in
        // SystemKey — normalize before matching, or the guard is silently dead.
        var vimKey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (vimKey is Key.H or Key.J or Key.K or Key.L
            && Keyboard.Modifiers is ModifierKeys.Alt or (ModifierKeys.Alt | ModifierKeys.Shift))
        {
            if (_viewModel.IsGridView)
            {
                switch (vimKey)
                {
                    case Key.H: _viewModel.MoveGridSelection(-1, 0); break;
                    case Key.L: _viewModel.MoveGridSelection(1, 0); break;
                    case Key.J: _viewModel.MoveGridSelection(0, 1); break;
                    case Key.K: _viewModel.MoveGridSelection(0, -1); break;
                }
                ScrollGridSelectedIntoView();
                e.Handled = true;
                return;
            }
            else if (vimKey is Key.J or Key.K)
            {
                if (vimKey == Key.J)
                    _viewModel.MoveSelectionDown();
                else
                    _viewModel.MoveSelectionUp();
                ScrollSelectedIntoView();
                e.Handled = true;
                return;
            }

            // List-mode H/L: no movement there — fall through to the switch below unhandled.
        }

        switch (e.Key)
        {
            case Key.Escape:
                HideWindow();
                e.Handled = true;
                break;

            case Key.Enter:
                ActivateSelectedWindow();
                e.Handled = true;
                break;

            case Key.Up:
                if (_viewModel.IsGridView)
                {
                    _viewModel.MoveGridSelection(0, -1);
                    ScrollGridSelectedIntoView();
                    e.Handled = true;
                    break;
                }
                _viewModel.MoveSelectionUp();
                ScrollSelectedIntoView();
                ScrollGridSelectedIntoView();
                e.Handled = true;
                break;

            case Key.Down:
                if (_viewModel.IsGridView)
                {
                    _viewModel.MoveGridSelection(0, 1);
                    ScrollGridSelectedIntoView();
                    e.Handled = true;
                    break;
                }
                _viewModel.MoveSelectionDown();
                ScrollSelectedIntoView();
                ScrollGridSelectedIntoView();
                e.Handled = true;
                break;

            case Key.Right:
                // When search box focused: plain Right moves the caret; Alt+Right groups.
                if (SearchBox.IsFocused && Keyboard.Modifiers != ModifierKeys.Alt)
                    break;
                if (_viewModel.IsGridView && Keyboard.Modifiers != ModifierKeys.Alt)
                {
                    _viewModel.MoveGridSelection(1, 0);
                    ScrollGridSelectedIntoView();
                    e.Handled = true;
                    break;
                }
                _viewModel.GroupByProcess();
                ScrollSelectedIntoView();
                e.Handled = true;
                break;

            case Key.Left:
                // When search box focused: plain Left moves the caret; Alt+Left ungroups.
                if (SearchBox.IsFocused && Keyboard.Modifiers != ModifierKeys.Alt)
                    break;
                if (_viewModel.IsGridView && Keyboard.Modifiers != ModifierKeys.Alt)
                {
                    _viewModel.MoveGridSelection(-1, 0);
                    ScrollGridSelectedIntoView();
                    e.Handled = true;
                    break;
                }
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
                ScrollGridSelectedIntoView();
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
        // Grid mode scrolls via ScrollGridSelectedIntoView; the ListBox is collapsed.
        if (_viewModel.IsGridView) return;

        if (_viewModel.SelectedWindow != null)
        {
            WindowList.ScrollIntoView(_viewModel.SelectedWindow);
        }
    }

    /// <summary>Grid counterpart of <see cref="ScrollSelectedIntoView"/>: no-op in list mode.</summary>
    private void ScrollGridSelectedIntoView()
    {
        if (!_viewModel.IsGridView || _viewModel.SelectedWindow == null) return;
        if (WindowGrid.ItemContainerGenerator.ContainerFromItem(_viewModel.SelectedWindow) is not ContentPresenter presenter)
            return;
        if (FindVisualDescendant<WindowCard>(presenter) is { } card)
            card.BringIntoView();
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

    private static T? FindVisualAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element != null)
        {
            if (element is T ancestor)
                return ancestor;

            element = element switch
            {
                Visual => VisualTreeHelper.GetParent(element),
                ContentElement contentElement => ContentOperations.GetParent(contentElement)
                    ?? (contentElement as FrameworkContentElement)?.Parent,
                _ => LogicalTreeHelper.GetParent(element)
            };
        }

        return null;
    }

    private static T? FindVisualDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;
            var nested = FindVisualDescendant<T>(child);
            if (nested != null)
                return nested;
        }
        return null;
    }

    private void WindowList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;

        // Interactive controls inside a row own their clicks. In particular, the search-mode
        // close button must not activate the target window before its Click event runs.
        if (FindVisualAncestor<ButtonBase>(source) != null)
            return;

        if (FindVisualAncestor<ListBoxItem>(source)?.DataContext is not AppWindow window)
            return;

        _viewModel.SelectedWindow = window;
        ActivateSelectedWindow();
    }

    private void WindowCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is WindowCard card && card.DataContext is AppWindow window)
        {
            ActivateCard(window);
        }
    }

    private void WindowGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Drives CardWidth from the real viewport so rows fill the full width —
        // the vertical scrollbar's width is accounted for automatically.
        _viewModel.UpdateCardWidth(e.NewSize.Width);
    }

    private void WindowCloseButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (!IsSearchMode || sender is not FrameworkElement { DataContext: AppWindow window })
            return;

        TryCloseWindow(window);
        SearchBox.Focus();
    }

    private void ViewModel_WindowActivated(object? sender, EventArgs e)
    {
        // Reset grouping state for next time
        _viewModel.ResetGrouping();
        HideWindow();
        // Reset the ignore flag after window is hidden
        _hotkeyService.SetIgnoreAltRelease(false);
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
            SettingsService.Instance.SettingsChanged -= OnSettingsChanged;
            _viewModel.Dispose();
            base.OnClosing(e);
            _hotkeyService.Dispose();
        }
    }

    public void ForceClose()
    {
        _isClosing = true;
        Close();
    }
}
