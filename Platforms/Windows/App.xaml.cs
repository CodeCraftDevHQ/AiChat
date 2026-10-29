namespace AiChat.WinUI;

public partial class App : MauiWinUIApplication
{
    public App()
    {
        this.InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => AiChat.MauiProgram.CreateMauiApp();
}
