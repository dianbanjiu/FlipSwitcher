using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using Switcheroo.Services;

namespace Switcheroo.Views;

/// <summary>
/// Settings window with Fluent 2 design
/// </summary>
public partial class SettingsWindow : Window
{
    private bool _isInitializing = true;

    public SettingsWindow()
    {
        InitializeComponent();
        LoadSettings();
        UpdateAdminStatusDisplay();
        _isInitializing = false;
    }

    private void LoadSettings()
    {
        var settings = SettingsService.Instance.Settings;
        AltSpaceCheckBox.IsChecked = settings.UseAltSpace;
        AltTabCheckBox.IsChecked = settings.UseAltTab;
        RunAsAdminCheckBox.IsChecked = settings.RunAsAdmin;
        
        // Sync startup setting with actual registry/Task Scheduler state
        bool actualStartupEnabled = StartupService.IsStartupEnabled();
        if (settings.StartWithWindows != actualStartupEnabled)
        {
            settings.StartWithWindows = actualStartupEnabled;
            SettingsService.Instance.Save();
        }
        StartWithWindowsCheckBox.IsChecked = settings.StartWithWindows;
        HideOnFocusLostCheckBox.IsChecked = settings.HideOnFocusLost;
        
        UpdateCurrentHotkeyDisplay();
    }

    private void UpdateAdminStatusDisplay()
    {
        bool isAdmin = AdminService.IsRunningAsAdmin();
        
        if (isAdmin)
        {
            AdminStatusBadge.Background = (Brush)FindResource("AccentDefaultBrush");
            AdminStatusText.Text = "Active";
            AdminStatusText.Foreground = (Brush)FindResource("TextOnAccentBrush");
        }
        else
        {
            AdminStatusBadge.Background = (Brush)FindResource("ControlDefaultBrush");
            AdminStatusText.Text = "Inactive";
            AdminStatusText.Foreground = (Brush)FindResource("TextSecondaryBrush");
        }
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
            var result = MessageBox.Show(
                wantAdmin 
                    ? "Switcheroo needs to restart with administrator privileges.\n\nRestart now?"
                    : "Switcheroo needs to restart without administrator privileges.\n\nRestart now?",
                "Restart Required",
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
                        "Failed to restart the application. Please restart manually.",
                        "Restart Failed",
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
            MessageBox.Show(
                enable 
                    ? "Failed to enable startup with Windows. Please try running as administrator."
                    : "Failed to disable startup with Windows.",
                "Switcheroo",
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

            MessageBox.Show("At least one hotkey must be enabled.", "Switcheroo",
                MessageBoxButton.OK, MessageBoxImage.Warning);
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
        Close();
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
}

