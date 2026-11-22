using Microsoft.Maui.Storage;

namespace AiChat.Services;

/// <summary>Look (accent colour, bubbles, spacing) and keyboard shortcuts.</summary>
public partial class ChatState
{
    public UiStyle Style { get; private set; } = new();
    public string StyleSignature => UiStyleHelper.VarsJson(Style);

    public Dictionary<string, string> Keys { get; private set; } = KeyBindings.Defaults();
    public string KeysSignature => System.Text.Json.JsonSerializer.Serialize(Keys);

    void LoadStyleAndKeys()
    {
        try { Style = UiStyleHelper.Normalize(System.Text.Json.JsonSerializer.Deserialize<UiStyle>(Preferences.Default.Get("ui_style", "")) ?? new()); }
        catch { Style = new(); }
        Keys = KeyBindings.Parse(Preferences.Default.Get("keys_json", ""));
    }

    public void SetStyle(Action<UiStyle> change)
    {
        change(Style);
        UiStyleHelper.Normalize(Style);
        Preferences.Default.Set("ui_style", System.Text.Json.JsonSerializer.Serialize(Style));
        Notify();
    }

    public void ResetStyle()
    {
        Style = new UiStyle();
        Preferences.Default.Remove("ui_style");
        Notify();
    }

    /// <summary>Gives an action a new combination ("" = off). Returns an error text when it cannot be used.</summary>
    public string? SetKey(string action, string combo)
    {
        if (!Keys.ContainsKey(action)) return null;
        if (combo.Length > 0)
        {
            var n = KeyBindings.Normalize(combo);
            if (n == null) return L["این ترکیب قابل استفاده نیست (باید Ctrl یا Alt داشته باشد)."];
            if (KeyBindings.Reserved(n) is { } why) return L.F("این ترکیب برای «{0}» رزرو است.", why);
            if (KeyBindings.Conflict(Keys, action, n) is { } other)
                return L.F("این ترکیب قبلاً برای «{0}» استفاده شده است.", L[KeyBindings.Actions.First(a => a.Id == other).Label]);
            combo = n;
        }
        Keys[action] = combo;
        Preferences.Default.Set("keys_json", KeyBindings.Serialize(Keys));
        Notify();
        return null;
    }

    public void ResetKeys()
    {
        Keys = KeyBindings.Defaults();
        Preferences.Default.Remove("keys_json");
        Notify();
    }
}
