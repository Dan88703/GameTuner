using GameTuner.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace GameTuner.App.Views;

public sealed partial class CleanupPage : Page
{
    public CleanupPage()
    {
        // The view model comes from the DI container (a singleton), so its
        // state survives navigating away and back. x:Bind in XAML binds to it.
        ViewModel = App.Services.GetRequiredService<CleanupViewModel>();
        InitializeComponent();
    }

    public CleanupViewModel ViewModel { get; }
}
