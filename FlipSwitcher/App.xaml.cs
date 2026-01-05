using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FlipSwitcher.Services;
using FlipSwitcher.Views;

namespace FlipSwitcher;

public partial class App : Application
{
    private TrayIconService? _trayIconService;
    private static Mutex? _mutex;
    private const string MutexName = "FlipSwitcher_SingleInstance_Mutex";

    /// <summary>
    /// Release the Mutex to allow a new instance to start
    /// </summary>
    public static void ReleaseMutexForRestart()
    {
        if (_mutex != null)
        {
            try
            {
                _mutex.ReleaseMutex();
                _mutex.Dispose();
                _mutex = null;
            }
            catch
            {
                // Ignore errors
            }
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Check if another instance is already running
        bool createdNew;
        _mutex = new Mutex(true, MutexName, out createdNew);

        if (!createdNew)
        {
            // Another instance is already running, exit directly
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // Check if we need to restart with or without admin privileges
        var settings = SettingsService.Instance.Settings;
        bool isAdmin = AdminService.IsRunningAsAdmin();

        if (settings.RunAsAdmin && !isAdmin)
        {
            // Setting says run as admin, but we're not admin - try to elevate
            if (AdminService.RestartAsAdmin())
            {
                _mutex?.ReleaseMutex();
                _mutex?.Dispose();
                Shutdown();
                return;
            }
            // If elevation failed (user cancelled UAC), continue running as normal user
            // but update the setting to reflect reality
            settings.RunAsAdmin = false;
            SettingsService.Instance.Save();
        }
        else if (!settings.RunAsAdmin && isAdmin)
        {
            // Setting says run as normal user, but we're admin - restart as normal user
            if (AdminService.RestartAsNormalUser())
            {
                _mutex?.ReleaseMutex();
                _mutex?.Dispose();
                Shutdown();
                return;
            }
        }

        // Initialize language service
        LanguageService.Instance.Initialize();

        // Initialize theme service and apply theme
        ThemeService.Instance.ApplyTheme((AppTheme)settings.Theme);

        // Apply font setting
        if (!string.IsNullOrWhiteSpace(settings.FontFamily))
        {
            FontService.Instance.ApplyFont(settings.FontFamily);
        }

        // Initialize services
        _trayIconService = new TrayIconService();

        // Check for updates on startup (delayed to avoid blocking)
        if (settings.CheckForUpdates)
        {
            const int UpdateCheckDelayMs = 3000;
            _ = Task.Run(async () =>
            {
                await Task.Delay(UpdateCheckDelayMs);
                await CheckForUpdatesAsync();
            });
        }

        // Set up global exception handling
        DispatcherUnhandledException += (s, args) =>
        {
            var message = string.Format(LanguageService.GetString("MsgErrorOccurred"), args.Exception.Message);
            FluentDialog.Show(message, LanguageService.GetString("MsgErrorTitle"),
                FluentDialogButton.OK, FluentDialogIcon.Error);
            args.Handled = true;
        };
    }

    private async Task CheckForUpdatesAsync()
    {
        var updateInfo = await UpdateService.Instance.CheckForUpdatesAsync(silent: true);
        if (updateInfo != null)
        {
            Dispatcher.Invoke(() =>
            {
                // Check if settings window is open, if so set its dialog flag
                var settingsWindow = FindOpenSettingsWindow();
                if (settingsWindow != null)
                {
                    settingsWindow.SetShowingDialog(true);
                }

                var message = string.Format(
                    LanguageService.GetString("MsgUpdateAvailable"),
                    updateInfo.Version);
                var result = FluentDialog.Show(
                    message,
                    LanguageService.GetString("MsgUpdateAvailableTitle"),
                    FluentDialogButton.YesNo,
                    FluentDialogIcon.Information,
                    settingsWindow);
                
                if (settingsWindow != null)
                {
                    settingsWindow.SetShowingDialog(false);
                }
                
                if (result == FluentDialogResult.Yes)
                {
                    UpdateService.Instance.OpenDownloadPage(updateInfo.DownloadUrl);
                }
            });
        }
    }

    private Views.SettingsWindow? FindOpenSettingsWindow()
    {
        foreach (Window window in Windows)
        {
            if (window is Views.SettingsWindow settingsWindow && settingsWindow.IsLoaded)
            {
                return settingsWindow;
            }
        }
        return null;
    }


    protected override void OnExit(ExitEventArgs e)
    {
        _trayIconService?.Dispose();
        UpdateService.Instance.Dispose();
        ThemeService.Instance.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
