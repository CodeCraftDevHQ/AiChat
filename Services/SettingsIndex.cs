namespace AiChat.Services;

/// <summary>One result of the settings search.</summary>
public record SettingsHit(string Section, string SectionTitle, string Anchor, string Title, int Score);

/// <summary>
/// Search over all the settings. A setting is found by its name in either language (Persian or English, whatever
/// the app language is), by the name of its section and by a few extra words people may type.
/// The list of settings is in SettingsIndex.Items.cs.
/// </summary>
public static partial class SettingsIndex
{
    /// <summary>Section key → Persian title (the same keys as the sections of the settings dialog).</summary>
    public static readonly (string Key, string Title, string Extra)[] Sections =
    {
        ("general", "عمومی", "language زبان theme dark light تم تاریک روشن rtl راست‌چین فارسی english انگلیسی"),
        ("appearance", "ظاهر", "font size width line height قلم فونت اندازه عرض متن"),
        ("api", "API ها", "provider openai claude anthropic gemini openrouter ollama lm studio server key token endpoint base url کلید مدل سرویس‌دهنده اتصال"),
        ("chat", "گفتگو", "system prompt files images attachments پیام سیستمی فایل عکس پیوست"),
        ("answer", "پاسخ مدل", "temperature top_p max tokens thinking reasoning stream دما فکر کردن طول پاسخ"),
        ("search", "جستجو در وب", "web search internet duckduckgo brave searxng google سرچ اینترنت موتور جستجو"),
        ("memory", "حافظه", "long-term memory remember حافظه‌ی بلندمدت یادآوری"),
        ("summary", "خلاصه‌سازی گفتگو", "summarize summary long chat context خلاصه گفتگوی طولانی"),
        ("personas", "شخصیت‌ها", "persona role character agent نقش شخصیت"),
        ("prompts", "کتابخانه‌ی پرامپت", "prompt library shortcuts slash command قالب دستور میانبر"),
        ("tools", "ابزارها", "tools function calling plugin javascript python code run calculator اجرای کد ماشین حساب تاریخ"),
        ("mcp", "سرورهای MCP", "model context protocol server stdio connector github filesystem"),
        ("customtools", "ابزارهای من", "custom tools my tools javascript http webhook command prompt template ابزار دلخواه ابزارهای من وب هوک خط فرمان قالب"),
        ("voice", "صدا", "voice speech tts stt read aloud microphone whisper text to speech صدا خواندن میکروفون گفتار تبدیل"),
        ("localserver", "سرور محلی (API و MCP)", "local server api openai compatible mcp token localhost port visual studio vscode cursor continue سرور محلی توکن پورت برنامه دیگر اتصال"),
        ("cost", "توکن و هزینه", "token cost price usage قیمت هزینه مصرف"),
        ("security", "امنیت و حریم خصوصی", "pin lock password privacy security قفل رمز پین امنیت حریم"),
        ("data", "داده‌ها و لاگ", "backup restore export import log data delete پشتیبان لاگ داده خروجی گرفتن"),
        ("about", "درباره", "about version update connector catalog اتصال کاتالوگ نسخه"),
    };

    /// <summary>Extra words for single settings (by their Persian text).</summary>
    static readonly Dictionary<string, string> Extra = new()
    {
        ["کلید API"] = "api key token secret password توکن رمز",
        ["قفل برنامه"] = "pin lock password fingerprint رمز پین اثر انگشت",
        ["زبان برنامه"] = "language persian english فارسی انگلیسی",
        ["تم برنامه"] = "dark mode light mode night حالت شب تاریک",
        ["نسخه پشتیبان"] = "backup restore export import بکاپ",
        ["لاگ برنامه"] = "log errors debug خطا",
        ["پرسیدن قبل از اجرای ابزار"] = "permission approve allow confirm اجازه تایید",
        ["اجرای کد جاوااسکریپت (در محیط ایزوله)"] = "run code js sandbox python powershell node cmd اجرای کد پایتون",
        ["تنظیمات اجرای کد"] = "code languages python powershell cmd node csharp زبان برنامه‌نویسی",
        ["پیام سیستمی (اختیاری، برای همه مدل‌ها)"] = "system prompt instruction دستور اولیه",
        ["نماد پول"] = "currency dollar toman تومان دلار",
        ["جدول قیمت مدل‌ها"] = "pricing price per million قیمت",
    };

    public static IEnumerable<(string Anchor, string Section, string Key)> All =>
        Sections.Select(s => ("", s.Key, s.Title)).Concat(Items);

    static string Norm(string? s) => SearchText.NormalizeQuery(s);

    /// <summary>Finds settings; every word of the query must appear (in the name, the section or the extra words).</summary>
    public static List<SettingsHit> Search(string? query, Loc loc, int max = 40)
    {
        var words = Norm(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = new List<SettingsHit>();
        if (words.Length == 0) return hits;

        var titles = Sections.ToDictionary(s => s.Key, s => s.Title);
        var extras = Sections.ToDictionary(s => s.Key, s => s.Extra);

        foreach (var (anchor, section, key) in All)
        {
            var en = Translations.En.TryGetValue(key, out var e) ? e : "";
            var secFa = titles[section];
            var secEn = Translations.En.TryGetValue(secFa, out var se) ? se : "";
            var own = Norm(key + " " + en + " " + (Extra.TryGetValue(key, out var x) ? x : ""));
            var sec = Norm(secFa + " " + secEn + " " + extras[section]);
            var isSection = anchor.Length == 0;
            var hay = isSection ? own + " " + sec : own;

            if (!words.All(w => hay.Contains(w, StringComparison.Ordinal))) continue;

            var title = Norm(loc[key]);
            var score = 0;
            if (title.StartsWith(words[0], StringComparison.Ordinal)) score += 50;
            if (words.All(w => title.Contains(w, StringComparison.Ordinal))) score += 30;
            if (isSection) score += 20;
            score -= title.Length / 8;
            hits.Add(new SettingsHit(section, loc[secFa], anchor, loc[key], score));
        }

        // settings of a section whose name matches but whose own name does not (e.g. "memory" → everything in Memory)
        foreach (var (anchor, section, key) in Items)
        {
            if (hits.Any(h => h.Anchor == anchor && h.Section == section && h.Title == loc[key])) continue;
            var sec = Norm(titles[section] + " " + (Translations.En.TryGetValue(titles[section], out var se) ? se : ""));
            if (words.All(w => sec.Contains(w, StringComparison.Ordinal)))
                hits.Add(new SettingsHit(section, loc[titles[section]], anchor, loc[key], -20));
        }

        return hits.OrderByDescending(h => h.Score).ThenBy(h => h.Title, StringComparer.Ordinal).Take(max).ToList();
    }
}
