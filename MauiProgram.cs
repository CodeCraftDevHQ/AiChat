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
        builder.Services.AddSingleton<StorageService>();
        builder.Services.AddSingleton<ChatState>();

        return builder.Build();
    }
}
