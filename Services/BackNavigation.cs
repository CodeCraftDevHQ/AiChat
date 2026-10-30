namespace AiChat.Services;

/// <summary>
/// Bridge between the Android back button (native side) and the Blazor UI.
/// The handler returns true when it closed something (dialog, settings page, sidebar).
/// </summary>
public static class BackNavigation
{
    public static Func<bool>? Handler { get; set; }

    /// <summary>True if the UI consumed the back press.</summary>
    public static bool TryHandle()
    {
        try { return Handler?.Invoke() ?? false; }
        catch { return false; }
    }
}
