using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WindowsWifiWpf.ViewModels;

namespace WindowsWifiWpf;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();

        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        Drop += OnDrop;
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.IsFileOverDropZone = true;
            }
            e.Handled = true;
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        // Désactive l'état d'illumination de la bordure néon quand le fichier quitte l'application
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.IsFileOverDropZone = false;
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.IsFileOverDropZone = false;

            if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                viewModel.AddFiles(files);
                e.Handled = true;
            }
        }
    }
}

public class StatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string status)
        {
            return status switch
            {
                "Terminé" or "Réussi" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#22C55E")), // Émeraude
                "Refusé" or "Annulé" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B")),  // Ambre
                "Échec" or "Erreur" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444")),   // Rouge corail
                _ => new SolidColorBrush(Colors.White)
            };
        }
        return new SolidColorBrush(Colors.White);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}