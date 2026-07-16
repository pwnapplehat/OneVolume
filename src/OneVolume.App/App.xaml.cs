using System.Windows;
using System.Windows.Threading;

namespace OneVolume.App;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single instance: a second launch just surfaces the existing one (via the tray).
        _singleInstance = new Mutex(initiallyOwned: true, "OneVolume.App.SingleInstance", out bool isNew);
        if (!isNew)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(
            System.Windows.Media.Color.FromRgb(0x4A, 0xDE, 0x80),
            Wpf.Ui.Appearance.ApplicationTheme.Dark);
        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        bool minimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase) || window.StartMinimizedPreferred;
        if (!minimized)
        {
            window.Show();
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            "OneVolume hit an unexpected error:\n\n" + e.Exception.Message,
            "OneVolume", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
