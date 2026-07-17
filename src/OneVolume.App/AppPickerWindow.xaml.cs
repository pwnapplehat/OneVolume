using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OneVolume.App.Services;
using Wpf.Ui.Controls;

namespace OneVolume.App;

/// <summary>
/// Modal picker over the installed-apps list (plus currently-playing processes).
/// If the search text matches nothing, it can still be added verbatim as a process
/// name — power users can type "someinternaltool" directly.
/// </summary>
public partial class AppPickerWindow : FluentWindow
{
    private readonly List<InstalledApp> _all;

    public InstalledApp? Selected { get; private set; }

    public AppPickerWindow(List<InstalledApp> apps)
    {
        InitializeComponent();
        _all = apps;
        ApplyFilter("");
        SearchBox.Focus();
    }

    private void ApplyFilter(string query)
    {
        string q = query.Trim();
        List<InstalledApp> filtered = q.Length == 0
            ? _all
            : [.. _all.Where(a =>
                a.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                a.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase))];

        AppList.ItemsSource = filtered;
        if (filtered.Count > 0)
        {
            AppList.SelectedIndex = 0;
        }

        CountText.Text = filtered.Count == 0
            ? $"No match — \"{q}\" will be added as a process name"
            : $"{filtered.Count} app(s)";
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter(SearchBox.Text);

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e) => OnAdd(sender, e);

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (AppList.SelectedItem is InstalledApp app)
        {
            Selected = app;
        }
        else
        {
            string typed = SearchBox.Text.Trim();
            if (typed.Length == 0)
            {
                return;
            }

            Selected = new InstalledApp(typed, typed);
        }

        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
