using System.ComponentModel;
using System.Windows;
using OneVolume.App.ViewModels;
using Wpf.Ui.Controls;

namespace OneVolume.App;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;
    private bool _reallyExit;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        // The tray icon uses the same .ico as the app.
        Tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        UpdateTrayToggleHeader();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.LevelingEnabled))
            {
                UpdateTrayToggleHeader();
            }
        };
    }

    public bool StartMinimizedPreferred => _viewModel.StartMinimizedPreferred;

    private void UpdateTrayToggleHeader()
        => TrayToggle.Header = _viewModel.LevelingEnabled ? "Pause leveling" : "Resume leveling";

    // Closing the window hides to tray; the app exits only via the tray menu.
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_reallyExit)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void OnTrayDoubleClick(object sender, RoutedEventArgs e) => ShowFromTray();

    private void OnTrayOpen(object sender, RoutedEventArgs e) => ShowFromTray();

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnTrayToggle(object sender, RoutedEventArgs e)
        => _viewModel.LevelingEnabled = !_viewModel.LevelingEnabled;

    private void OnTrayExit(object sender, RoutedEventArgs e)
    {
        _reallyExit = true;
        Tray.Dispose();
        _viewModel.Dispose(); // stops the engine and restores all session volumes
        Close();
        Application.Current.Shutdown();
    }
}
