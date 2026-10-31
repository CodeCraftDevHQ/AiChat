namespace AiChat.Services;

/// <summary>
/// UI translation. The Persian text is the key: <c>L["گفتگوی جدید"]</c> returns the Persian text in
/// Persian mode and the English one from <see cref="Translations"/> in English mode.
/// A missing translation falls back to the Persian text.
/// </summary>
public class Loc
{
    public string Language { get; private set; } = "fa";

    public bool IsEnglish => Language == "en";

    public string this[string fa] =>
        IsEnglish && Translations.En.TryGetValue(fa, out var en) ? en : fa;

    /// <summary>Translated text with {0}, {1}... placeholders filled in.</summary>
    public string F(string fa, params object?[] args) => string.Format(this[fa], args);

    public void SetLanguage(string lang)
    {
        Language = lang == "en" ? "en" : "fa";
        MarkdownRenderer.SetLabels(this["کپی"], this["ذخیره"], this["کپی شد ✓"], this["ناموفق"]);
    }
}
