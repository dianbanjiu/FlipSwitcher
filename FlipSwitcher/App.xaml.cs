using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FlipSwitcher.Services;

namespace FlipSwitcher;

public partial class App : Application
{
    private HotkeyService? _hotkeyService;
    private TrayIconService? _trayIconService;
    private static Mutex? _mutex;
    private const string MutexName = "FlipSwitcher_SingleInstance_Mutex";

    protected override void OnStartup(StartupEventArgs e)
    {
        // 检查是否已有实例运行
        bool createdNew;
        _mutex = new Mutex(true, MutexName, out createdNew);

        if (!createdNew)
        {
            // 已有实例运行，直接退出
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // Check if we need to restart with admin privileges
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

        // Initialize language service
        LanguageService.Instance.Initialize();

        // Initialize theme service and apply theme
        ThemeService.Instance.ApplyTheme((AppTheme)settings.Theme);

        // Initialize services
        _hotkeyService = new HotkeyService();
        _trayIconService = new TrayIconService();

        // Check for updates on startup (delayed to avoid blocking)
        if (settings.CheckForUpdates)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);
                await CheckForUpdatesAsync();
            });
        }

        // Set up global exception handling
        DispatcherUnhandledException += (s, args) =>
        {
            var message = string.Format(LanguageService.GetString("MsgErrorOccurred"), args.Exception.Message);
            MessageBox.Show(message, LanguageService.GetString("MsgErrorTitle"),
                MessageBoxButton.OK, MessageBoxImage.Error);
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
                var message = string.Format(
                    LanguageService.GetString("MsgUpdateAvailable"),
                    updateInfo.Version);
                var result = MessageBox.Show(
                    message,
                    LanguageService.GetString("MsgUpdateAvailableTitle"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);
                
                if (result == MessageBoxResult.Yes)
                {
                    UpdateService.Instance.OpenDownloadPage(updateInfo.DownloadUrl);
                }
            });
        }
    }


    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeyService?.Dispose();
        _trayIconService?.Dispose();
        UpdateService.Instance.Dispose();
        ThemeService.Instance.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
