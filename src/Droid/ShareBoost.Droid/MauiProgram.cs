using Microsoft.Extensions.Logging;
using ShareBoost.Droid.Services;
using ShareBoost.Droid.ViewModels;
using WindowsWifiLibrary.Interfaces;

namespace ShareBoost.Droid
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

            builder.Services.AddSingleton<AndroidWifiDirectService>();
            builder.Services.AddSingleton<IWifiDirectService>(services => services.GetRequiredService<AndroidWifiDirectService>());
            builder.Services.AddSingleton<MainViewModel>();
            builder.Services.AddSingleton<MainPage>();
            builder.Services.AddSingleton<AppShell>();

#if DEBUG
            builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
