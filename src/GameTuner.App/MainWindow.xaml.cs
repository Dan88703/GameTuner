using GameTuner.App.Views;
using GameTuner.Core.Settings;
using GameTuner.Core.Tuning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace GameTuner.App;

public sealed partial class MainWindow : Window
{
    private bool _closingForReal;

    public MainWindow()
    {
        InitializeComponent();

        // Draw our own title bar (icon + name) on top of the Mica background.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.Resize(new SizeInt32(1200, 820));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Closing += OnClosing;

        if (Elevation.IsElevated)
        {
            AdminBadge.Visibility = Visibility.Visible;
            Title = "GameTuner (администратор)";
        }

        ContentFrame.Navigate(typeof(DashboardPage));
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    /// <summary>Brings the window back from the tray (or from behind other windows).</summary>
    public void ShowAndActivate()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
    }

    /// <summary>Called by App.ExitApp: closes without hiding to the tray.</summary>
    public void CloseForReal()
    {
        _closingForReal = true;
        Close();
    }

    /// <summary>
    /// The X button hides the window to the tray (if enabled in settings), so
    /// auto mode keeps working in the background. Real exit is in the tray menu.
    /// </summary>
    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closingForReal) return;

        bool toTray = App.Services.GetRequiredService<ISettingsService>().Current.MinimizeToTray;
        if (toTray)
        {
            args.Cancel = true;
            sender.Hide();
        }
        else
        {
            // Full exit restores the power plan, stops sensors etc. Run it after
            // this event handler returns, not from inside it.
            args.Cancel = true;
            DispatcherQueue.TryEnqueue(((App)Application.Current).ExitApp);
        }
    }

    private void Nav_Loaded(object sender, RoutedEventArgs e)
    {
        // The built-in settings item is labelled "Settings" in English.
        if (Nav.SettingsItem is NavigationViewItem settings) settings.Content = "Настройки";
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        Type page = args.IsSettingsSelected
            ? typeof(SettingsPage)
            : (args.SelectedItemContainer?.Tag as string) switch
            {
                "games" => typeof(GamesPage),
                "charts" => typeof(ChartsPage),
                "cleanup" => typeof(CleanupPage),
                "optimization" => typeof(OptimizationPage),
                _ => typeof(DashboardPage)
            };

        if (ContentFrame.CurrentSourcePageType != page) ContentFrame.Navigate(page);
    }
}
