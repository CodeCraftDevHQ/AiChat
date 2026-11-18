using AiChat.Services;

namespace AiChat.Tests;

public class PromptLibraryTests
{
    [Fact]
    public void ToJson_Parse_RoundTrip_KeepsUnicodeAndNewlines()
    {
        var list = new List<Shortcut>
        {
            new("fa", "ترجمه کن:\n\n{{text}}", "ترجمه"),
            new("fix", "Fix \"quotes\"", ""),
        };
        var json = ShortcutHelper.ToJson(list);
        Assert.Contains("ترجمه کن", json);
        var back = ShortcutHelper.Parse(json, out var err);
        Assert.Null(err);
        Assert.Equal(list, back);
    }

    [Fact]
    public void Samples_AreValid_AndUniqueNames()
    {
        var all = ShortcutHelper.Parse(ShortcutHelper.Sample, out var e1).Concat(ShortcutHelper.MoreSamples()).ToList();
        Assert.Null(e1);
        Assert.Equal(all.Count, all.Select(x => x.Name.ToLowerInvariant()).Distinct().Count());
        Assert.All(all, x => Assert.DoesNotContain(' ', x.Name));
        var again = ShortcutHelper.Parse(ShortcutHelper.ToJson(all), out var e2);
        Assert.Null(e2);
        Assert.Equal(all.Count, again.Count);
    }
}
