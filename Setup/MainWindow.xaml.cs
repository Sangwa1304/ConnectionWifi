using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Setup;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window, INotifyPropertyChanged
{
    private int _Percentage = 0;
    public int Percentage
    {
        get => _Percentage;
        set
        {
            _Percentage = value;
            OnPropertyChnaged(nameof(Percentage));
        }
    }
    private string _Path = "";
    public string PathProcess
    {
        get => _Path;
        set
        {
            _Path = value;
            OnPropertyChnaged(nameof(PathProcess));
        }
    }
    public MainWindow()
    {
        InitializeComponent();
    }


    public void OnPropertyChnaged(string name) => PropertyChanged?.Invoke(this, new(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}