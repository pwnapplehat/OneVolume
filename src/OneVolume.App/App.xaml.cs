using System.Windows;
using System.Windows.Threading;

namespace OneVolume.App;

public partial class App : Application
{
    private const string ShowSignalName = "OneVolume.App.ShowSignal";

    private Mutex? _singleInstance;
    private EventWaitHandle? _showSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single instance: a second launch signals the first one to show its window and
        // exits — launching the exe again must never look like "nothing happened".
        _singleInstance = new Mutex(initiallyOwned: true, "OneVolume.App.SingleInstance", out bool isNew);
        if (!isNew)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(ShowSignalName);
                signal.Set();
            }
            catch
            {
                // First instance is mid-startup or mid-exit — nothing sensible to do.
            }

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

        StartShowSignalListener(window);
    }

    /// <summary>Background wait on the named event; each signal surfaces the window.</summary>
    private void StartShowSignalListener(MainWindow window)
    {
        _showSignal = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ShowSignalName);
        var listener = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    _showSignal.WaitOne();
                    Dispatcher.Invoke(window.ShowFromTray);
                }
                catch
                {
                    return; // handle disposed during shutdown
                }
            }
        })
        {
            IsBackground = true,
            Name = "OneVolume.ShowSignal",
        };
        listener.Start();
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
        _showSignal?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
