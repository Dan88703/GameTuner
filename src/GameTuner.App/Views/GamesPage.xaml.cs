using GameTuner.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace GameTuner.App.Views;

public sealed partial class GamesPage : Page
{
    public GamesPage()
    {
        // The view model comes from the DI container (a singleton), so its
        // state survives navigating away and back. x:Bind in XAML binds to it.
        ViewModel = App.Services.GetRequiredService<GamesViewModel>();
        InitializeComponent();
    }

    public GamesViewModel ViewModel { get; }
}
