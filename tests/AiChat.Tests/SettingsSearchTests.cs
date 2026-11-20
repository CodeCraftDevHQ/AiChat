using AiChat.Services;

namespace AiChat.Tests;

public class SettingsSearchTests
{
    static Loc Fa() { var l = new Loc(); l.SetLanguage("fa"); return l; }
    static Loc En() { var l = new Loc(); l.SetLanguage("en"); return l; }

    [Fact]
    public void Finds_by_persian_name_with_variant_letters_and_digits()
    {
        var hits = SettingsIndex.Search("ماشین حساب", Fa());
        Assert.Contains(hits, h => h.Section == "tools" && h.Title.Contains("ماشین"));
        // Arabic ي/ك are the same as ی/ک
        Assert.NotEmpty(SettingsIndex.Search("كليد", Fa()));
    }

    [Fact]
    public void Finds_by_english_name_even_in_persian_mode_and_the_other_way()
    {
        Assert.Contains(SettingsIndex.Search("calculator", Fa()), h => h.Section == "tools");
        Assert.Contains(SettingsIndex.Search("calculator", En()), h => h.Title == "Calculator");
        Assert.Contains(SettingsIndex.Search("ماشین‌حساب", En()), h => h.Title == "Calculator");
    }

    [Fact]
    public void Section_name_and_extra_words_lead_to_the_section()
    {
        Assert.Contains(SettingsIndex.Search("backup", En()), h => h.Section == "data");
        Assert.Contains(SettingsIndex.Search("پین", Fa()), h => h.Section == "security");
        Assert.Contains(SettingsIndex.Search("python", En()), h => h.Section == "tools");
    }

    [Fact]
    public void All_words_must_match_and_nothing_gives_an_empty_list()
    {
        Assert.Empty(SettingsIndex.Search("zzzzqqq", En()));
        Assert.Empty(SettingsIndex.Search("   ", En()));
        Assert.Empty(SettingsIndex.Search(null, En()));
        Assert.NotEmpty(SettingsIndex.Search("web search", En()));
    }

    [Fact]
    public void Every_item_points_to_a_known_section_and_has_a_translation_or_is_persian_only()
    {
        var sections = SettingsIndex.Sections.Select(s => s.Key).ToHashSet();
        foreach (var (anchor, section, key) in SettingsIndex.All)
        {
            Assert.Contains(section, sections);
            Assert.False(string.IsNullOrWhiteSpace(key));
            Assert.True(anchor.Length == 0 || anchor.StartsWith("set"));
        }
        var anchors = SettingsIndex.All.Where(a => a.Anchor.Length > 0).Select(a => a.Anchor).ToList();
        Assert.Equal(anchors.Count, anchors.Distinct().Count());
    }

    [Fact]
    public void Tools_and_code_are_on_by_default()
    {
        var o = new ToolOptions();
        Assert.True(o.Enabled);
        Assert.True(o.CodeRun);
        Assert.True(o.CodeLanguages.First(l => l.Id == "javascript").Enabled);
        Assert.True(o.CodeLanguages.First(l => l.Id == "javascript").Ask);
    }
}
