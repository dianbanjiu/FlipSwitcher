using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Navigation;
using FlipSwitcher.Core;
using FlipSwitcher.Services;

namespace FlipSwitcher.Views;

/// <summary>
/// Settings window with Fluent 2 design
/// </summary>
public partial class SettingsWindow : Window
{
    private bool _isInitializing = true;
    private bool _isClosing = false;
    private bool _isShowingDialog = false;
    private bool _isRestarting = false;
    private HotkeyService? _hotkeyService;

    /// <summary>
    /// Set whether a dialog is being shown, used to prevent the window from closing when showing a dialog
    /// </summary>
    public void SetShowingDialog(bool showing)
    {
        _isShowingDialog = showing;
    }

    public SettingsWindow()
    {
        InitializeComponent();
        LoadFontFamilies();
        LoadSettings();
        UpdateAdminStatusDisplay();
        UpdateVersionDisplay();
        _isInitializing = false;
    }

    public SettingsWindow(HotkeyService? hotkeyService = null) : this()
    {
        _hotkeyService = hotkeyService;
        // If HotkeyService is not provided, try to get it from MainWindow
        if (_hotkeyService == null && Application.Current.MainWindow is MainWindow mainWindow)
        {
            _hotkeyService = mainWindow.HotkeyService;
        }
        if (_hotkeyService != null)
        {
            _hotkeyService.EscapePressed += HotkeyService_EscapePressed;
        }

        // Listen for window deactivation event
        Deactivated += SettingsWindow_Deactivated;

        // Ensure window gets focus after content is rendered (fixes first-open focus issue)
        ContentRendered += SettingsWindow_ContentRendered;
    }

    private void SettingsWindow_ContentRendered(object? sender, EventArgs e)
    {
        // Only need to handle once
        ContentRendered -= SettingsWindow_ContentRendered;

        // Force activate using same technique as MainWindow
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            ForceActivateWindow();
        }));
    }

    private void ForceActivateWindow()
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        // Simulate Alt key to allow SetForegroundWindow
        NativeMethods.keybd_event(NativeMethods.VK_ALT, 0, NativeMethods.KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_ALT, 0, NativeMethods.KEYEVENTF_EXTENDEDKEY | NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);

        NativeMethods.SetForegroundWindow(hwnd);
        NativeMethods.BringWindowToTop(hwnd);

        Activate();
        Focus();
    }

    private void UpdateVersionDisplay()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var versionStr = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : LanguageService.GetString("VersionPreview");
        VersionText.Text = string.Format(LanguageService.GetString("VersionFormat"), versionStr);
    }

    private string DefaultFontDisplayName => LanguageService.GetString("DefaultFontDisplay");

    private void LoadFontFamilies()
    {
        var fonts = FontService.Instance.GetInstalledFonts();
        FontFamilyComboBox.Items.Clear();
        FontFamilyComboBox.Items.Add(DefaultFontDisplayName);

        foreach (var font in fonts)
        {
            FontFamilyComboBox.Items.Add(font);
        }
    }

    private void LoadSettings()
    {
        var settings = SettingsService.Instance.Settings;
        AltSpaceCheckBox.IsChecked = settings.UseAltSpace;
        AltTabCheckBox.IsChecked = settings.UseAltTab;
        RunAsAdminCheckBox.IsChecked = settings.RunAsAdmin;

        // Load language setting
        LanguageComboBox.SelectedIndex = settings.Language;

        // Load font setting
        const int DefaultFontIndex = 0;
        if (string.IsNullOrWhiteSpace(settings.FontFamily))
        {
            FontFamilyComboBox.SelectedIndex = DefaultFontIndex;
        }
        else
        {
            var fontIndex = FontFamilyComboBox.Items.IndexOf(settings.FontFamily);
            if (fontIndex >= 0)
            {
                FontFamilyComboBox.SelectedIndex = fontIndex;
            }
            else
            {
                FontFamilyComboBox.SelectedIndex = 0;
            }
        }

        // Sync startup setting with actual registry/Task Scheduler state
        bool actualStartupEnabled = StartupService.IsStartupEnabled();
        if (settings.StartWithWindows != actualStartupEnabled)
        {
            settings.StartWithWindows = actualStartupEnabled;
            SettingsService.Instance.Save();
        }
        StartWithWindowsCheckBox.IsChecked = settings.StartWithWindows;
        HideOnFocusLostCheckBox.IsChecked = settings.HideOnFocusLost;
        PinyinSearchCheckBox.IsChecked = settings.EnablePinyinSearch;
        ThemeComboBox.SelectedIndex = settings.Theme;
        CheckForUpdatesCheckBox.IsChecked = settings.CheckForUpdates;

        UpdateCurrentHotkeyDisplay();
    }

    private void UpdateAdminStatusDisplay()
    {
        // Display the actual state of the current process
        bool isAdmin = AdminService.IsRunningAsAdmin();

        if (isAdmin)
        {
            AdminStatusBadge.Background = (Brush)FindResource("AccentDefaultBrush");
            AdminStatusText.Text = LanguageService.GetString("SettingsAdminEnabled");
            AdminStatusText.Foreground = (Brush)FindResource("TextOnAccentBrush");
        }
        else
        {
            AdminStatusBadge.Background = (Brush)FindResource("ControlDefaultBrush");
            AdminStatusText.Text = LanguageService.GetString("SettingsAdminDisabled");
            AdminStatusText.Foreground = (Brush)FindResource("TextSecondaryBrush");
        }
    }

    private void SaveSetting<T>(Action<AppSettings, T> setter, T value, Action? afterSave = null)
    {
        if (_isInitializing) return;
        var settings = SettingsService.Instance.Settings;
        setter(settings, value);
        SettingsService.Instance.Save();
        afterSave?.Invoke();
    }

    private void LanguageComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        SaveSetting((s, v) => s.Language = v, LanguageComboBox.SelectedIndex, () =>
        {
            LanguageService.Instance.SetLanguage((AppLanguage)SettingsService.Instance.Settings.Language);
            UpdateAdminStatusDisplay();
        });
    }

    private void RunAsAdminCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        bool wantAdmin = RunAsAdminCheckBox.IsChecked == true;
        bool isCurrentlyAdmin = AdminService.IsRunningAsAdmin();

        // Save the setting
        var settings = SettingsService.Instance.Settings;
        settings.RunAsAdmin = wantAdmin;
        SettingsService.Instance.Save();

        // Update startup registration if enabled (switches between registry and Task Scheduler)
        if (settings.StartWithWindows)
        {
            StartupService.SetStartupEnabled(true);
        }

        // If no restart needed, just update display
        if (wantAdmin == isCurrentlyAdmin)
        {
            UpdateAdminStatusDisplay();
            return;
        }

        // Prompt for restart
        _isShowingDialog = true;
        var message = wantAdmin
            ? LanguageService.GetString("MsgRestartRequired")
            : LanguageService.GetString("MsgRestartRequiredNormal");
        var result = FluentDialog.Show(
            message,
            LanguageService.GetString("MsgRestartRequiredTitle"),
            FluentDialogButton.YesNo,
            FluentDialogIcon.Question,
            this);
        _isShowingDialog = false;

        if (result != FluentDialogResult.Yes)
        {
            UpdateAdminStatusDisplay();
            return;
        }

        if (_isRestarting) return;
        _isRestarting = true;

        App.ReleaseMutexForRestart();

        bool restartSuccess = wantAdmin
            ? AdminService.RestartAsAdmin()
            : AdminService.RestartAsNormalUser();

        if (restartSuccess)
        {
            Application.Current.Shutdown();
        }
        else
        {
            _isRestarting = false;
            _isShowingDialog = true;
            FluentDialog.Show(
                LanguageService.GetString("MsgRestartFailed"),
                LanguageService.GetString("MsgRestartFailedTitle"),
                FluentDialogButton.OK,
                FluentDialogIcon.Warning,
                this);
            _isShowingDialog = false;
            UpdateAdminStatusDisplay();
        }
    }

    private void StartWithWindowsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        bool enable = StartWithWindowsCheckBox.IsChecked == true;
        var settings = SettingsService.Instance.Settings;
        settings.StartWithWindows = enable;
        SettingsService.Instance.Save();

        if (!StartupService.SetStartupEnabled(enable))
        {
            ShowStartupErrorMessage(enable);
            RevertStartupCheckbox(enable);
        }
    }

    private void ShowStartupErrorMessage(bool enable)
    {
        _isShowingDialog = true;
        var message = enable
            ? LanguageService.GetString("MsgStartupFailed")
            : LanguageService.GetString("MsgStartupDisabledFailed");
        FluentDialog.Show(
            message,
            LanguageService.GetString("AppTitle"),
            FluentDialogButton.OK,
            FluentDialogIcon.Warning,
            this);
        _isShowingDialog = false;
    }

    private void RevertStartupCheckbox(bool enable)
    {
        _isInitializing = true;
        StartWithWindowsCheckBox.IsChecked = !enable;
        SettingsService.Instance.Settings.StartWithWindows = !enable;
        SettingsService.Instance.Save();
        _isInitializing = false;
    }

    private void HideOnFocusLostCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        SaveSetting((s, v) => s.HideOnFocusLost = v, HideOnFocusLostCheckBox.IsChecked == true);
    }

    private void PinyinSearchCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        SaveSetting((s, v) => s.EnablePinyinSearch = v, PinyinSearchCheckBox.IsChecked == true);
    }

    private void ThemeComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        SaveSetting((s, v) => s.Theme = v, ThemeComboBox.SelectedIndex, () =>
        {
            ThemeService.Instance.ApplyTheme((AppTheme)ThemeComboBox.SelectedIndex);
            UpdateAdminStatusDisplay();
        });
    }

    private void FontFamilyComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        var selectedItem = FontFamilyComboBox.SelectedItem?.ToString();
        var fontFamily = (selectedItem == DefaultFontDisplayName || string.IsNullOrWhiteSpace(selectedItem))
            ? string.Empty
            : selectedItem!;

        SaveSetting((s, v) => s.FontFamily = v, fontFamily, () =>
        {
            FontService.Instance.ApplyFont(fontFamily);
        });
    }

    private void CheckForUpdatesCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        SaveSetting((s, v) => s.CheckForUpdates = v, CheckForUpdatesCheckBox.IsChecked == true);
    }

    private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        SetUpdateButtonState(isChecking: true);

        try
        {
            var updateInfo = await UpdateService.Instance.CheckForUpdatesAsync(silent: false);
            if (updateInfo != null)
            {
                ShowUpdateAvailableDialog(updateInfo);
            }
            else
            {
                ShowNoUpdateDialog();
            }
        }
        finally
        {
            SetUpdateButtonState(isChecking: false);
        }
    }

    private void SetUpdateButtonState(bool isChecking)
    {
        CheckForUpdatesButton.IsEnabled = !isChecking;
        CheckForUpdatesButton.Content = isChecking
            ? LanguageService.GetString("SettingsCheckingUpdates")
            : LanguageService.GetString("SettingsCheckForUpdates");
    }

    private void ShowUpdateAvailableDialog(UpdateInfo updateInfo)
    {
        var message = string.Format(
            LanguageService.GetString("MsgUpdateAvailable"),
            updateInfo.Version);
        _isShowingDialog = true;
        var result = FluentDialog.Show(
            message,
            LanguageService.GetString("MsgUpdateAvailableTitle"),
            FluentDialogButton.YesNo,
            FluentDialogIcon.Information,
            this);
        _isShowingDialog = false;

        if (result == FluentDialogResult.Yes)
        {
            UpdateService.Instance.OpenDownloadPage(updateInfo.DownloadUrl);
        }
    }

    private void ShowNoUpdateDialog()
    {
        _isShowingDialog = true;
        FluentDialog.Show(
            LanguageService.GetString("MsgNoUpdateAvailable"),
            LanguageService.GetString("MsgNoUpdateAvailableTitle"),
            FluentDialogButton.OK,
            FluentDialogIcon.Information,
            this);
        _isShowingDialog = false;
    }

    private void HotkeyCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        // Ensure at least one hotkey is selected
        if (AltSpaceCheckBox.IsChecked != true && AltTabCheckBox.IsChecked != true)
        {
            // Revert the change
            if (sender == AltSpaceCheckBox)
                AltSpaceCheckBox.IsChecked = true;
            else
                AltTabCheckBox.IsChecked = true;

            FluentDialog.Show(
                LanguageService.GetString("MsgAtLeastOneHotkey"),
                LanguageService.GetString("AppTitle"),
                FluentDialogButton.OK,
                FluentDialogIcon.Warning,
                this);
            return;
        }

        SaveSetting((s, v) => s.UseAltSpace = v, AltSpaceCheckBox.IsChecked == true);
        SaveSetting((s, v) => s.UseAltTab = v, AltTabCheckBox.IsChecked == true);
        UpdateCurrentHotkeyDisplay();
    }

    private void UpdateCurrentHotkeyDisplay()
    {
        var hotkeys = new List<string>();

        if (AltSpaceCheckBox.IsChecked == true)
            hotkeys.Add(LanguageService.GetString("SettingsAltSpace"));

        if (AltTabCheckBox.IsChecked == true)
            hotkeys.Add(LanguageService.GetString("SettingsAltTab"));

        CurrentHotkeyText.Text = hotkeys.Count > 0
            ? string.Join(LanguageService.GetString("HotkeySeparator"), hotkeys)
            : LanguageService.GetString("HotkeyNone");
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isClosing)
        {
            _isClosing = true;
            Close();
        }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = e.Uri.AbsoluteUri,
            UseShellExecute = true
        });
        e.Handled = true;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Support Esc or Alt+Esc to close (even if Alt key is not released)
        if (e.Key == Key.Escape && !_isClosing)
        {
            _isClosing = true;
            Close();
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_isClosing)
        {
            _isClosing = true;
            Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private void HotkeyService_EscapePressed(object? sender, EventArgs e)
    {
        // Close settings window when Alt+Esc is pressed
        if (!_isClosing && IsLoaded)
        {
            _isClosing = true;
            Close();
        }
    }

    private void SettingsWindow_Deactivated(object? sender, EventArgs e)
    {
        // Close when window loses focus, but need to check if already closing or showing a dialog
        if (!_isClosing && !_isShowingDialog && IsLoaded)
        {
            _isClosing = true;
            // Use Dispatcher to delay closing, avoiding calls during window closing process
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsLoaded)
                {
                    Close();
                }
            }), System.Windows.Threading.DispatcherPriority.Normal);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Deactivated -= SettingsWindow_Deactivated;
        if (_hotkeyService != null)
        {
            _hotkeyService.EscapePressed -= HotkeyService_EscapePressed;
        }
        base.OnClosed(e);
    }
}

