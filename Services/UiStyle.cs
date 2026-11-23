using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiChat.Services;

/// <summary>User's look: accent colour, own colour of the user's bubble, bubble shape and spacing.</summary>
public class UiStyle
{
    /// <summary>"#rrggbb" or "" (= the theme's colour).</summary>
    public string Accent { get; set; } = "";
    public string UserBubble { get; set; } = "";
    public int Radius { get; set; } = 22;
    /// <summary>"compact" | "normal" | "roomy"</summary>
    public string Pad { get; set; } = "normal";
    /// <summary>Space between messages (px).</summary>
    public int Gap { get; set; } = 18;
    /// <summary>Max width of the user's bubble (percent).</summary>
    public int BubbleMax { get; set; } = 85;

    public UiStyle Clone() => (UiStyle)MemberwiseClone();
}

public static class UiStyleHelper
{
    public static readonly (string Name, string Hex)[] AccentPresets =
    {
        ("Green", "#10a37f"), ("Blue", "#2f7cf6"), ("Indigo", "#6366f1"), ("Purple", "#a855f7"),
        ("Pink", "#ec4899"), ("Red", "#ef4444"), ("Orange", "#f97316"), ("Amber", "#d99a06"), ("Teal", "#0d9488"), ("Gray", "#8b949e")
    };

    /// <summary>Themes: system, light, dark and four more.</summary>
    public static readonly string[] Themes = { "system", "light", "dark", "amoled", "midnight", "sepia", "nord" };

    public static string NormalizeTheme(string? t) => Themes.Contains(t ?? "") ? t! : "system";

    public static bool IsDarkTheme(string t) => t is "dark" or "amoled" or "midnight" or "nord";

    static readonly Regex Hex = new("^#?([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$", RegexOptions.Compiled);

    /// <summary>"#rrggbb" (lower case) or null when it is not a colour.</summary>
    public static string? NormalizeHex(string? s)
    {
        var m = Hex.Match((s ?? "").Trim());
        if (!m.Success) return null;
        var h = m.Groups[1].Value.ToLowerInvariant();
        if (h.Length == 3) h = string.Concat(h.Select(c => new string(c, 2)));
        return "#" + h;
    }

    /// <summary>Readable text colour on this background (relative luminance).</summary>
    public static string OnColor(string hex)
    {
        var h = NormalizeHex(hex) ?? "#000000";
        double Ch(int i) { var v = int.Parse(h.Substring(1 + i * 2, 2), NumberStyles.HexNumber) / 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        var lum = 0.2126 * Ch(0) + 0.7152 * Ch(1) + 0.0722 * Ch(2);
        return lum > 0.4 ? "#111111" : "#ffffff";
    }

    public static UiStyle Normalize(UiStyle s)
    {
        s.Accent = NormalizeHex(s.Accent) ?? "";
        s.UserBubble = NormalizeHex(s.UserBubble) ?? "";
        s.Radius = Math.Clamp(s.Radius, 0, 32);
        s.Pad = s.Pad is "compact" or "roomy" ? s.Pad : "normal";
        s.Gap = Math.Clamp(s.Gap, 6, 48);
        s.BubbleMax = Math.Clamp(s.BubbleMax, 50, 100);
        return s;
    }

    /// <summary>CSS variables to set; a null value means "remove the variable" (back to the theme's value).</summary>
    public static Dictionary<string, string?> Vars(UiStyle s)
    {
        s = Normalize(s.Clone());
        var (py, px) = s.Pad switch { "compact" => (6, 14), "roomy" => (14, 22), _ => (10, 18) };
        return new Dictionary<string, string?>
        {
            ["--accent"] = s.Accent.Length > 0 ? s.Accent : null,
            ["--accent-fg"] = s.Accent.Length > 0 ? OnColor(s.Accent) : null,
            ["--user-bg"] = s.UserBubble.Length > 0 ? s.UserBubble : null,
            ["--user-fg"] = s.UserBubble.Length > 0 ? OnColor(s.UserBubble) : null,
            ["--bubble-r"] = s.Radius + "px",
            ["--bubble-py"] = py + "px",
            ["--bubble-px"] = px + "px",
            ["--msg-gap"] = s.Gap + "px",
            ["--bubble-max"] = s.BubbleMax + "%"
        };
    }

    public static string VarsJson(UiStyle s) => JsonSerializer.Serialize(Vars(s));
}

/// <summary>One thing a keyboard shortcut can do.</summary>
public record KeyAction(string Id, string Label, string Default);

public static class KeyBindings
{
    public static readonly KeyAction[] Actions =
    {
        new("newchat", "گفتگوی جدید", "Ctrl+Shift+O"),
        new("settings", "باز کردن تنظیمات", "Ctrl+,"),
        new("sidebar", "نمایش / پنهان کردن نوار کناری", "Ctrl+B"),
        new("focus", "رفتن به کادر پیام", "Ctrl+/"),
        new("stop", "توقف پاسخ", "Ctrl+."),
        new("regenerate", "تولید دوباره‌ی پاسخ", "Alt+R"),
        new("theme", "تغییر تم تاریک / روشن", "Ctrl+Shift+L"),
        new("prompts", "کتابخانه‌ی پرامپت", "Ctrl+Shift+P"),
        new("gallery", "گالری", "Ctrl+Shift+G"),
        new("compare", "مقایسه‌ی دو مدل", "Ctrl+Shift+M"),
        new("modelswitch", "تعویض سریع مدل", "Ctrl+K"),
    };

    static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["esc"] = "Escape", ["escape"] = "Escape", ["del"] = "Delete", ["delete"] = "Delete", ["space"] = "Space", ["spacebar"] = "Space",
        ["enter"] = "Enter", ["return"] = "Enter", ["tab"] = "Tab", ["backspace"] = "Backspace", ["home"] = "Home", ["end"] = "End",
        ["pageup"] = "PageUp", ["pagedown"] = "PageDown", ["up"] = "ArrowUp", ["down"] = "ArrowDown", ["left"] = "ArrowLeft", ["right"] = "ArrowRight",
        ["arrowup"] = "ArrowUp", ["arrowdown"] = "ArrowDown", ["arrowleft"] = "ArrowLeft", ["arrowright"] = "ArrowRight", ["insert"] = "Insert"
    };

    static readonly Dictionary<string, string> CodeKeys = new()
    {
        ["Comma"] = ",", ["Period"] = ".", ["Slash"] = "/", ["Semicolon"] = ";", ["Quote"] = "'", ["BracketLeft"] = "[",
        ["BracketRight"] = "]", ["Backslash"] = "\\", ["Minus"] = "-", ["Equal"] = "=", ["Backquote"] = "`"
    };

    static string? KeyName(string k)
    {
        if (k == " ") return "Space";
        if (k.Length == 1) return char.IsLetter(k[0]) ? k.ToUpperInvariant() : k;
        if (Names.TryGetValue(k, out var n)) return n;
        if (Regex.IsMatch(k, @"^[Ff](1[0-2]|[1-9])$")) return k.ToUpperInvariant();
        return null;
    }

    /// <summary>"ctrl+shift+o" → "Ctrl+Shift+O". Null when it is not a usable shortcut (no key, or a plain letter that would block typing).</summary>
    public static string? Normalize(string? combo)
    {
        if (string.IsNullOrWhiteSpace(combo)) return null;
        var parts = combo.Trim().Split('+').Select(p => p.Trim()).ToList();
        if (parts.Any(p => p.Length == 0)) return null;
        bool ctrl = false, alt = false, shift = false;
        foreach (var m in parts.Take(parts.Count - 1))
        {
            switch (m.ToLowerInvariant())
            {
                case "ctrl": case "control": case "cmd": case "command": case "meta": ctrl = true; break;
                case "alt": case "option": alt = true; break;
                case "shift": shift = true; break;
                default: return null;
            }
        }
        var key = KeyName(parts[^1]);
        if (key == null) return null;
        var isF = Regex.IsMatch(key, @"^F\d+$");
        if (!ctrl && !alt && !isF) return null; // a letter / Shift+letter alone would be typed text
        return (ctrl ? "Ctrl+" : "") + (alt ? "Alt+" : "") + (shift ? "Shift+" : "") + key;
    }

    /// <summary>The combination of a key press (same rules as the JavaScript side: letters and digits by physical key, so Persian layout works).</summary>
    public static string? FromEvent(string key, string code, bool ctrl, bool alt, bool shift, bool meta)
    {
        string? k;
        if (code.StartsWith("Key", StringComparison.Ordinal) && code.Length == 4) k = code[3].ToString();
        else if (code.StartsWith("Digit", StringComparison.Ordinal) && code.Length == 6) k = code[5].ToString();
        else if (CodeKeys.TryGetValue(code, out var c)) k = c;
        else k = KeyName(key);
        if (k == null) return null;
        var parts = new List<string>();
        if (ctrl || meta) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        parts.Add(k);
        return Normalize(string.Join("+", parts));
    }

    /// <summary>Why a combination must not be used (it is used by the system or by the app itself), or null.</summary>
    public static string? Reserved(string combo) => combo switch
    {
        "Ctrl+C" or "Ctrl+V" or "Ctrl+X" or "Ctrl+A" or "Ctrl+Z" or "Ctrl+Y" => "copy / paste / undo",
        "Ctrl+F" => "search in the chat",
        "Ctrl+W" or "Ctrl+Q" or "Alt+F4" or "Ctrl+Shift+Z" => "window",
        _ => null
    };

    public static Dictionary<string, string> Defaults() => Actions.ToDictionary(a => a.Id, a => a.Default);

    /// <summary>Defaults with the user's changes ("" = switched off) from a JSON object.</summary>
    public static Dictionary<string, string> Parse(string? json)
    {
        var map = Defaults();
        if (string.IsNullOrWhiteSpace(json)) return map;
        try
        {
            var user = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (user == null) return map;
            foreach (var kv in user)
            {
                if (!map.ContainsKey(kv.Key)) continue;
                if (string.IsNullOrWhiteSpace(kv.Value)) { map[kv.Key] = ""; continue; }
                var n = Normalize(kv.Value);
                if (n != null && Reserved(n) == null) map[kv.Key] = n;
            }
        }
        catch (JsonException) { }
        // two actions must not share one combination: the first keeps it
        var seen = new HashSet<string>();
        foreach (var a in Actions)
            if (map[a.Id].Length > 0 && !seen.Add(map[a.Id])) map[a.Id] = "";
        return map;
    }

    /// <summary>Only what differs from the defaults is saved.</summary>
    public static string Serialize(Dictionary<string, string> map)
    {
        var diff = map.Where(kv => Defaults().TryGetValue(kv.Key, out var d) && d != kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
        return JsonSerializer.Serialize(diff);
    }

    /// <summary>The action that already uses this combination (other than <paramref name="action"/>), or null.</summary>
    public static string? Conflict(Dictionary<string, string> map, string action, string combo) =>
        map.FirstOrDefault(kv => kv.Key != action && kv.Value == combo).Key;
}
