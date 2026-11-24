namespace ShareBoost.Droid;

public partial class AppShell : Shell
{
    public AppShell(MainPage mainPage)
    {
        InitializeComponent();
        Items.Add(new ShellContent
        {
            Title = "Partage",
            Route = "share",
            Content = mainPage
        });
    }
}
