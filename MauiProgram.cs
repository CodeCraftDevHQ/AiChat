using AiChat.Services;
using Microsoft.Extensions.Logging;

namespace AiChat;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        // One HttpClient for all providers. Streaming responses can be long, so no global timeout;
        // cancellation is handled per request.
        builder.Services.AddSingleton(new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
        builder.Services.AddSingleton<ProviderFactory>();
        builder.Services.AddSingleton<LogService>();
        builder.Services.AddSingleton<StorageService>();
        builder.Services.AddSingleton<FileExportService>();
        builder.Services.AddSingleton<ChatState>();

        var app = builder.Build();

        var log = app.Services.GetRequiredService<LogService>();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Error("Unhandled exception: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Error("Unobserved task exception: " + e.Exception.GetBaseException());
            e.SetObserved();
        };
        log.Info($"App started. {AppInfo.Current.Name} {AppInfo.Current.VersionString} on " +
                 $"{DeviceInfo.Current.Platform} {DeviceInfo.Current.VersionString} ({DeviceInfo.Current.Model})");

        return app;
    }
}
