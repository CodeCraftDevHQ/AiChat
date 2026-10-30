using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AiChat.Services;
using AndroidX.Activity;

namespace AiChat;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
                           ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        OnBackPressedDispatcher.AddCallback(this, new BackCallback(this));
    }

    /// <summary>
    /// Back goes one layer up inside the app (section -> settings list -> chat).
    /// Only when nothing is open does it fall through to the system (leave the app).
    /// </summary>
    sealed class BackCallback : OnBackPressedCallback
    {
        readonly MainActivity _activity;

        public BackCallback(MainActivity activity) : base(true) => _activity = activity;

        public override void HandleOnBackPressed()
        {
            if (BackNavigation.TryHandle()) return;

            Enabled = false;
            _activity.OnBackPressedDispatcher.OnBackPressed();
            Enabled = true;
        }
    }
}
