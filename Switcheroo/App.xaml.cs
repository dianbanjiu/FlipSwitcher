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

