using System;
using System.Drawing;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;

namespace FlipSwitcher.Services;

/// <summary>
/// Service for managing the system tray icon
/// </summary>
public class TrayIconService : IDisposable
{
    private TaskbarIcon? _trayIcon;

    public TrayIconService()
    {
        InitializeTrayIcon();
    }

    private void InitializeTrayIcon()
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "FlipSwitcher - Press Alt+Space to switch windows",
            Visibility = Visibility.Visible
        };

        // Try to load icon from resources, fallback to system icon
        try
        {
            var iconUri = new Uri("pack://application:,,,/Assets/flipswitcher.png", UriKind.Absolute);
            var iconStream = System.Windows.Application.GetResourceStream(iconUri);
            if (iconStream != null)
            {
                // Convert PNG to Icon
                using var bitmap = new System.Drawing.Bitmap(iconStream.Stream);
                _trayIcon.Icon = Icon.FromHandle(bitmap.GetHicon());
            }
            else
            {
                _trayIcon.Icon = SystemIcons.Application;
            }
        }
        catch
        {
            // Use system application icon as fallback
            _trayIcon.Icon = SystemIcons.Application;
        }

        // Create context menu
        var contextMenu = new System.Windows.Controls.ContextMenu();

        var showItem = new System.Windows.Controls.MenuItem { Header = "Show FlipSwitcher" };
        showItem.Click += (s, e) => ShowMainWindow();

        var settingsItem = new System.Windows.Controls.MenuItem { Header = "Settings" };
        settingsItem.Click += (s, e) => ShowSettings();

        var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exitItem.Click += (s, e) => ExitApplication();

        contextMenu.Items.Add(showItem);
        contextMenu.Items.Add(new System.Windows.Controls.Separator());
        contextMenu.Items.Add(settingsItem);
        contextMenu.Items.Add(new System.Windows.Controls.Separator());
        contextMenu.Items.Add(exitItem);

        _trayIcon.ContextMenu = contextMenu;
        _trayIcon.TrayMouseDoubleClick += (s, e) => ShowMainWindow();
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
        var settingsWindow = new FlipSwitcher.Views.SettingsWindow();
        settingsWindow.Owner = System.Windows.Application.Current.MainWindow;
        settingsWindow.ShowDialog();
    }

    private void ExitApplication()
    {
        _trayIcon?.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    public void Dispose()
    {
        _trayIcon?.Dispose();
    }
}

