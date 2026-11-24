using ServiceTcpChared;
using ShareBoost.Droid.ViewModels;

namespace ShareBoost.Droid;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel _viewModel;

    public MainPage(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = viewModel;
        _viewModel.IncomingTransferRequested += OnIncomingTransferRequestedAsync;
    }

    private async Task<bool> OnIncomingTransferRequestedAsync(InfoMetas request)
    {
        var names = string.Join("\n", request.Metas.Take(8).Select(item => $"• {item.NameWithExtension}"));
        if (request.Metas.Length > 8)
            names += $"\n… et {request.Metas.Length - 8} autre(s) fichier(s)";
        var size = FormatSize(request.Length);
        return await DisplayAlert(
            "Réception de fichiers",
            $"Un appareil souhaite envoyer {request.Metas.Length} fichier(s) ({size}) :\n\n{names}\n\nAutoriser la réception ?",
            "Accepter",
            "Refuser");
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} o";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.##} Ko";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024):0.##} Mo";
        return $"{bytes / (1024d * 1024 * 1024):0.##} Go";
    }

    protected override void OnDisappearing()
    {
        _viewModel.IncomingTransferRequested -= OnIncomingTransferRequestedAsync;
        base.OnDisappearing();
    }
}
