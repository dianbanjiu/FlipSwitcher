using System;
using System.Threading;
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

        // Initialize services
        _hotkeyService = new HotkeyService();
        _trayIconService = new TrayIconService();

        // Set up global exception handling
        DispatcherUnhandledException += (s, args) =>
        {
            MessageBox.Show($"An error occurred: {args.Exception.Message}", "FlipSwitcher Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }


    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeyService?.Dispose();
        _trayIconService?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
