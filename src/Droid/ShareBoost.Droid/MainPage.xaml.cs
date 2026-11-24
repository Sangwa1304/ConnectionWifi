using ShareBoost.Droid.ViewModels;

namespace ShareBoost.Droid;

public partial class MainPage : ContentPage
{
    public MainPage(MainViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
