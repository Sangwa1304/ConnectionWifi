using System.Windows;
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
        Drop += OnDrop;
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
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

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && DataContext is MainViewModel viewModel)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                viewModel.AddFiles(files);
                e.Handled = true;
            }
        }
    }
}