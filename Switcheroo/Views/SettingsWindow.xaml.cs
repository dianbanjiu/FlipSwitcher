using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
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
        _isInitializing = false;
    }

    private void LoadSettings()
    {
        var settings = SettingsService.Instance.Settings;
        AltSpaceCheckBox.IsChecked = settings.UseAltSpace;
        AltTabCheckBox.IsChecked = settings.UseAltTab;
        UpdateCurrentHotkeyDisplay();
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
}

