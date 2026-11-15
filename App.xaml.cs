using Microsoft.Extensions.DependencyInjection;
using AiChat.Services;

namespace AiChat;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage()) { Title = "AI Chat" };

        // app lock: remember when the window was left, lock again after the chosen time
        ChatState? State() => IPlatformApplication.Current?.Services.GetService<ChatState>();
        window.Deactivated += (_, _) => State()?.OnLeft();
        window.Activated += (_, _) => State()?.OnReturned();
        window.Stopped += (_, _) => State()?.OnLeft();
        window.Resumed += (_, _) => State()?.OnReturned();
        return window;
    }
}
