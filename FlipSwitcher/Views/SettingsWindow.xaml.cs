using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
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
    private HotkeyService? _hotkeyService;

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
        // 如果没有传递 HotkeyService，尝试从 MainWindow 获取
        if (_hotkeyService == null && Application.Current.MainWindow is MainWindow mainWindow)
        {
            _hotkeyService = mainWindow.HotkeyService;
        }
        if (_hotkeyService != null)
        {
            _hotkeyService.EscapePressed += HotkeyService_EscapePressed;
        }
        
        // 监听窗口失去焦点事件
        Deactivated += SettingsWindow_Deactivated;
    }

    private void UpdateVersionDisplay()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var versionStr = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "preview";
        VersionText.Text = $"FlipSwitcher v{versionStr}";
    }

    private void LoadFontFamilies()
    {
        var fonts = FontService.Instance.GetInstalledFonts();
        FontFamilyComboBox.Items.Clear();
        
        // 添加默认选项
        FontFamilyComboBox.Items.Add("默认 (Segoe UI Variable)");
        
        // 添加系统字体
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
        if (string.IsNullOrWhiteSpace(settings.FontFamily))
        {
            FontFamilyComboBox.SelectedIndex = 0; // 默认字体
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
        MicaEffectCheckBox.IsChecked = settings.EnableMicaEffect;
        ThemeComboBox.SelectedIndex = settings.Theme;
        CheckForUpdatesCheckBox.IsChecked = settings.CheckForUpdates;
        
        UpdateCurrentHotkeyDisplay();
    }

    private void UpdateAdminStatusDisplay()
    {
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

    private void LanguageComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        var settings = SettingsService.Instance.Settings;
        settings.Language = LanguageComboBox.SelectedIndex;
        SettingsService.Instance.Save();

        // Apply language change
        LanguageService.Instance.SetLanguage((AppLanguage)settings.Language);
        
        // Update admin status text with new language
        UpdateAdminStatusDisplay();
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

        // If the setting changed and requires a restart
        if (wantAdmin != isCurrentlyAdmin)
        {
            var message = wantAdmin 
                ? LanguageService.GetString("MsgRestartRequired")
                : LanguageService.GetString("MsgRestartRequiredNormal");
            var result = MessageBox.Show(
                message,
                LanguageService.GetString("MsgRestartRequiredTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                bool restartSuccess = wantAdmin 
                    ? AdminService.RestartAsAdmin() 
                    : AdminService.RestartAsNormalUser();

                if (restartSuccess)
                {
                    Application.Current.Shutdown();
                }
                else
                {
                    MessageBox.Show(
                        LanguageService.GetString("MsgRestartFailed"),
                        LanguageService.GetString("MsgRestartFailedTitle"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
        }
    }

    private void StartWithWindowsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        bool enable = StartWithWindowsCheckBox.IsChecked == true;

        // Save setting first
        var settings = SettingsService.Instance.Settings;
        settings.StartWithWindows = enable;
        SettingsService.Instance.Save();

        // Update registry/Task Scheduler
        bool success = StartupService.SetStartupEnabled(enable);
        
        if (!success)
        {
            var message = enable 
                ? LanguageService.GetString("MsgStartupFailed")
                : LanguageService.GetString("MsgStartupDisabledFailed");
            MessageBox.Show(
                message,
                LanguageService.GetString("AppTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            // Revert the checkbox
            _isInitializing = true;
            StartWithWindowsCheckBox.IsChecked = !enable;
            settings.StartWithWindows = !enable;
            SettingsService.Instance.Save();
            _isInitializing = false;
        }
    }

    private void HideOnFocusLostCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        var settings = SettingsService.Instance.Settings;
        settings.HideOnFocusLost = HideOnFocusLostCheckBox.IsChecked == true;
        SettingsService.Instance.Save();
    }

    private void MicaEffectCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        var settings = SettingsService.Instance.Settings;
        settings.EnableMicaEffect = MicaEffectCheckBox.IsChecked == true;
        SettingsService.Instance.Save();
    }

    private void ThemeComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        var settings = SettingsService.Instance.Settings;
        settings.Theme = ThemeComboBox.SelectedIndex;
        SettingsService.Instance.Save();
    }

    private void FontFamilyComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        var settings = SettingsService.Instance.Settings;
        var selectedItem = FontFamilyComboBox.SelectedItem?.ToString();
        
        if (selectedItem == "默认 (Segoe UI Variable)" || string.IsNullOrWhiteSpace(selectedItem))
        {
            settings.FontFamily = string.Empty;
        }
        else
        {
            settings.FontFamily = selectedItem;
        }
        
        SettingsService.Instance.Save();
        FontService.Instance.ApplyFont(settings.FontFamily);
    }

    private void CheckForUpdatesCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        var settings = SettingsService.Instance.Settings;
        settings.CheckForUpdates = CheckForUpdatesCheckBox.IsChecked == true;
        SettingsService.Instance.Save();
    }

    private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        CheckForUpdatesButton.IsEnabled = false;
        CheckForUpdatesButton.Content = LanguageService.GetString("SettingsCheckingUpdates");

        try
        {
            var updateInfo = await UpdateService.Instance.CheckForUpdatesAsync(silent: false);
            if (updateInfo != null)
            {
                var message = string.Format(
                    LanguageService.GetString("MsgUpdateAvailable"),
                    updateInfo.Version);
                _isShowingDialog = true;
                var result = MessageBox.Show(
                    message,
                    LanguageService.GetString("MsgUpdateAvailableTitle"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);
                _isShowingDialog = false;

                if (result == MessageBoxResult.Yes)
                {
                    // 打开浏览器后允许窗口正常关闭
                    UpdateService.Instance.OpenDownloadPage(updateInfo.DownloadUrl);
                }
            }
            else
            {
                _isShowingDialog = true;
                MessageBox.Show(
                    LanguageService.GetString("MsgNoUpdateAvailable"),
                    LanguageService.GetString("MsgNoUpdateAvailableTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                _isShowingDialog = false;
            }
        }
        finally
        {
            CheckForUpdatesButton.IsEnabled = true;
            CheckForUpdatesButton.Content = LanguageService.GetString("SettingsCheckForUpdates");
            _isShowingDialog = false;
        }
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

            MessageBox.Show(
                LanguageService.GetString("MsgAtLeastOneHotkey"), 
                LanguageService.GetString("AppTitle"),
                MessageBoxButton.OK, 
                MessageBoxImage.Warning);
            return;
        }

        // Save settings
        var settings = SettingsService.Instance.Settings;
        settings.UseAltSpace = AltSpaceCheckBox.IsChecked == true;
        settings.UseAltTab = AltTabCheckBox.IsChecked == true;
        SettingsService.Instance.Save();

        UpdateCurrentHotkeyDisplay();
    }

    private void UpdateCurrentHotkeyDisplay()
    {
        var hotkeys = new List<string>();
        
        if (AltSpaceCheckBox.IsChecked == true)
            hotkeys.Add("Alt + Space");
        
        if (AltTabCheckBox.IsChecked == true)
            hotkeys.Add("Alt + Tab");

        CurrentHotkeyText.Text = hotkeys.Count > 0 
            ? string.Join(" / ", hotkeys) 
            : "None";
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
        // 支持 Esc 或 Alt+Esc 关闭（即使 Alt 键未松开）
        if (e.Key == Key.Escape && !_isClosing)
        {
            _isClosing = true;
            Close();
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // 使用 OnKeyDown 作为备用，确保 Alt+Esc 能被捕获
        if (e.Key == Key.Escape && !_isClosing)
        {
            // 检查 Alt 键是否被按下（即使 WPF 的 Keyboard.Modifiers 可能检测不到）
            bool altPressed = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0 ||
                              (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LMENU) & 0x8000) != 0 ||
                              (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RMENU) & 0x8000) != 0;
            
            // 无论 Alt 键是否被按下，都关闭窗口
            _isClosing = true;
            Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private void HotkeyService_EscapePressed(object? sender, EventArgs e)
    {
        // 当 Alt+Esc 被按下时关闭设置窗口
        if (!_isClosing && IsLoaded)
        {
            _isClosing = true;
            Close();
        }
    }

    private void SettingsWindow_Deactivated(object? sender, EventArgs e)
    {
        // 当窗口失去焦点时关闭，但需要检查是否已经在关闭过程中或正在显示对话框
        if (!_isClosing && !_isShowingDialog && IsLoaded)
        {
            _isClosing = true;
            // 使用 Dispatcher 延迟关闭，避免在窗口关闭过程中调用
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

