using System.Windows;
using Switcheroo.Services;

namespace Switcheroo;

public partial class App : Application
{
    private HotkeyService? _hotkeyService;
    private TrayIconService? _trayIconService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Check if we need to restart with admin privileges
        var settings = SettingsService.Instance.Settings;
        bool isAdmin = AdminService.IsRunningAsAdmin();

        if (settings.RunAsAdmin && !isAdmin)
        {
            // Setting says run as admin, but we're not admin - try to elevate
            if (AdminService.RestartAsAdmin())
            {
                Shutdown();
                return;
            }
            // If elevation failed (user cancelled UAC), continue running as normal user
            // but update the setting to reflect reality
            settings.RunAsAdmin = false;
            SettingsService.Instance.Save();
        }

        // Initialize services
        _hotkeyService = new HotkeyService();
        _trayIconService = new TrayIconService();

        // Set up global exception handling
        DispatcherUnhandledException += (s, args) =>
        {
            MessageBox.Show($"An error occurred: {args.Exception.Message}", "Switcheroo Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeyService?.Dispose();
        _trayIconService?.Dispose();
        base.OnExit(e);
    }
}

