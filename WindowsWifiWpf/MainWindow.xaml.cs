using System.Windows;
using WindowsWifiWpf.ViewModels;

namespace WindowsWifiWpf;

public partial class MainWindow : Window 
{ 
    public MainWindow()
    {
        InitializeComponent();
        // Using MVVM: set the DataContext to the MainViewModel which owns services and state.
        DataContext = new MainViewModel();
    }
}
