using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;

namespace FlipSwitcher.Services;

/// <summary>
/// Service for managing the system tray icon
/// </summary>
public class TrayIconService : IDisposable
{
    private const string IconResourcePath = "pack://application:,,,/Assets/flipswitcher.png";
    private const string ToolTipText = "FlipSwitcher";

    private TaskbarIcon? _trayIcon;
    private System.Windows.Controls.MenuItem? _showItem;
    private System.Windows.Controls.MenuItem? _settingsItem;
    private System.Windows.Controls.MenuItem? _restartItem;
    private System.Windows.Controls.MenuItem? _exitItem;

    public TrayIconService()
    {
        InitializeTrayIcon();
        LanguageService.Instance.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        UpdateMenuTexts();
    }

    private void UpdateMenuTexts()
    {
        if (_showItem != null)
            _showItem.Header = LanguageService.GetString("TrayShow");
        if (_settingsItem != null)
            _settingsItem.Header = LanguageService.GetString("TraySettings");
        if (_restartItem != null)
            _restartItem.Header = LanguageService.GetString("TrayRestart");
        if (_exitItem != null)
            _exitItem.Header = LanguageService.GetString("TrayExit");
    }

    private void InitializeTrayIcon()
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = ToolTipText,
            Visibility = Visibility.Visible,
            Icon = LoadTrayIcon()
        };

        // Create context menu
        var contextMenu = new System.Windows.Controls.ContextMenu();

        _showItem = new System.Windows.Controls.MenuItem { Header = LanguageService.GetString("TrayShow") };
        _showItem.Click += (s, e) => ShowMainWindow();

        _settingsItem = new System.Windows.Controls.MenuItem { Header = LanguageService.GetString("TraySettings") };
        _settingsItem.Click += (s, e) => ShowSettings();

        _restartItem = new System.Windows.Controls.MenuItem { Header = LanguageService.GetString("TrayRestart") };
        _restartItem.Click += (s, e) => RestartApplication();

        _exitItem = new System.Windows.Controls.MenuItem { Header = LanguageService.GetString("TrayExit") };
        _exitItem.Click += (s, e) => ExitApplication();

        contextMenu.Items.Add(_showItem);
        contextMenu.Items.Add(new System.Windows.Controls.Separator());
        contextMenu.Items.Add(_settingsItem);
        contextMenu.Items.Add(_restartItem);
        contextMenu.Items.Add(new System.Windows.Controls.Separator());
        contextMenu.Items.Add(_exitItem);

        _trayIcon.ContextMenu = contextMenu;
        _trayIcon.TrayMouseDoubleClick += (s, e) => ShowMainWindow();
    }

    private Icon LoadTrayIcon()
    {
        try
        {
            var iconUri = new Uri(IconResourcePath, UriKind.Absolute);
            var iconStream = Application.GetResourceStream(iconUri);
            if (iconStream != null)
            {
                using var bitmap = new System.Drawing.Bitmap(iconStream.Stream);
                return Icon.FromHandle(bitmap.GetHicon());
            }
        }
        catch
        {
            // Fall through to default icon
        }
        return SystemIcons.Application;
    }

    private void ShowMainWindow()
    {
        var mainWindow = System.Windows.Application.Current.MainWindow;
        if (mainWindow != null)
        {
            mainWindow.Show();
            mainWindow.Activate();
        }
    }

    private void ShowSettings()
    {
        var mainWindow = Application.Current.MainWindow as Views.MainWindow;
        var hotkeyService = mainWindow?.HotkeyService;
        
        hotkeyService?.SetSettingsWindowOpen(true);
        
        var settingsWindow = new Views.SettingsWindow(hotkeyService)
        {
            Owner = Application.Current.MainWindow
        };
        
        settingsWindow.Closed += (s, e) => hotkeyService?.SetSettingsWindowOpen(false);
        settingsWindow.ShowDialog();
    }

    private void RestartApplication()
    {
        try
        {
            var exePath = ProcessHelper.GetExecutablePath();
            if (string.IsNullOrEmpty(exePath))
            {
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true
            };

            Process.Start(startInfo);
            
            // Close current application
            _trayIcon?.Dispose();
            System.Windows.Application.Current.Shutdown();
        }
        catch
        {
            // Ignore errors
        }
    }

    private void ExitApplication()
    {
        _trayIcon?.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    public void Dispose()
    {
        LanguageService.Instance.LanguageChanged -= OnLanguageChanged;
        _trayIcon?.Dispose();
    }
}

