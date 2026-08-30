using System.Windows;

namespace WindowsWifiWpf;

public partial class App : Application
{
    private void Application_Startup(object sender, StartupEventArgs e)
    {
        var mainWindow = new MainWindow();
        mainWindow.Show();
    }
}

