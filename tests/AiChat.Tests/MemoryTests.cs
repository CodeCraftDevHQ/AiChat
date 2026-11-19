using System.Text.Json.Nodes;
using AiChat.Services;

namespace AiChat.Tests;

public class MemoryTests
{
    static MemoryItem M(string text, int ageMinutes = 0, bool on = true) =>
        new() { Text = text, Created = DateTime.UtcNow.AddMinutes(-ageMinutes), Enabled = on };

    [Fact]
    public void BuildPrompt_NewestFirst_SkipsDisabled_AndEmptyWhenNothing()
    {
        var o = new MemoryOptions();
        Assert.Equal("", MemoryHelper.BuildPrompt(new List<MemoryItem>(), o));
        Assert.Equal("", MemoryHelper.BuildPrompt(new[] { M("x", on: false) }, o));

        var p = MemoryHelper.BuildPrompt(new[] { M("old", 60), M("new", 1), M("off", 5, false) }, o);
        Assert.StartsWith(MemoryHelper.DefaultHeader, p);
        Assert.DoesNotContain("- off", p);
        Assert.True(p.IndexOf("- new", StringComparison.Ordinal) < p.IndexOf("- old", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildPrompt_RespectsBudget_AndCustomHeader()
    {
        var items = Enumerable.Range(0, 50).Select(i => M(new string('a', 100) + i, i)).ToList();
        var p = MemoryHelper.BuildPrompt(items, new MemoryOptions { MaxChars = 600, Header = "MEM:" });
        Assert.StartsWith("MEM:\n", p);
        Assert.True(p.Length <= 600);
        Assert.Contains("a" + 0, p.Replace(new string('a', 99), ""));   // the newest item is kept
        Assert.DoesNotContain(new string('a', 100) + "49", p);        // the oldest was cut
    }

    [Fact]
    public void Normalize_And_Duplicates()
    {
        Assert.Equal("a b c", MemoryHelper.Normalize("  a \n b\t c "));
        Assert.Equal(301, MemoryHelper.Normalize(new string('x', 500)).Length);
        Assert.True(MemoryHelper.IsDuplicate(new[] { M("I like tea") }, "  i LIKE   tea "));
        Assert.False(MemoryHelper.IsDuplicate(new[] { M("I like tea") }, "I like coffee"));
    }

    [Fact]
    public void Forget_ByText_ById_AndRefusesTooManyMatches()
    {
        var list = new List<MemoryItem> { M("likes tea"), M("likes coffee"), M("lives in Tehran") };
        Assert.Empty(MemoryHelper.Forget(list, "zzz"));
        Assert.Empty(MemoryHelper.Forget(list, ""));
        var gone = MemoryHelper.Forget(list, "TEHRAN");
        Assert.Single(gone);
        Assert.Equal(2, list.Count);
        Assert.Empty(MemoryHelper.Forget(list, "likes", maxAtOnce: 1));   // 2 matches > 1: nothing is deleted
        Assert.Equal(2, list.Count);
        Assert.Single(MemoryHelper.Forget(list, list[0].Id));
    }

    [Fact]
    public void ParseSuggestions_ToleratesChatterAndDuplicates()
    {
        var r = MemoryHelper.ParseSuggestions("Sure!\n```json\n[\"Uses C#\", \"uses c#\", \" \", \"Speaks Persian\"]\n```");
        Assert.Equal(new[] { "Uses C#", "Speaks Persian" }, r);
        Assert.Empty(MemoryHelper.ParseSuggestions("nothing"));
        Assert.Empty(MemoryHelper.ParseSuggestions("[not json"));
        Assert.Empty(MemoryHelper.ParseSuggestions("[]"));
        Assert.Equal(2, MemoryHelper.ParseSuggestions("[\"a\",\"b\",\"c\"]", max: 2).Count);
    }

    [Fact]
    public void ConversationText_KeepsTheLastMessagesAndCutsLongOnes()
    {
        var msgs = Enumerable.Range(1, 40).Select(i => (i % 2 == 0 ? "assistant" : "user", "m" + i)).ToList();
        var t = MemoryHelper.ConversationText(msgs, maxMessages: 4);
        Assert.DoesNotContain("m36", t);
        Assert.Contains("User: m37", t);
        Assert.Contains("Assistant: m40", t);
        Assert.EndsWith("…", MemoryHelper.ConversationText(new[] { ("user", new string('x', 50)) }, maxCharsEach: 10));
    }

    [Fact]
    public void Json_RoundTrip_AndImportSkipsDuplicates()
    {
        var items = new List<MemoryItem> { M("one"), M("two", on: false) };
        var back = MemoryHelper.FromJson(MemoryHelper.ToJson(items), new List<MemoryItem>());
        Assert.Equal(new[] { "one", "two" }, back.Select(x => x.Text));
        Assert.False(back[1].Enabled);
        Assert.NotEqual(items[0].Id, back[0].Id);

        var plain = MemoryHelper.FromJson("[\"one\", \"three\"]", items);
        Assert.Equal(new[] { "three" }, plain.Select(x => x.Text));
        Assert.Throws<FormatException>(() => MemoryHelper.FromJson("{}", items));
    }

    [Fact]
    public async Task RememberAndForgetTools_CallTheHooks()
    {
        var saved = new List<string>();
        var hooks = new MemoryHooks { Ask = true, Remember = t => { saved.Add(t); return "Saved."; }, Forget = t => "Deleted: " + t };
        var set = BuiltInTools.Build(new ToolOptions { Enabled = true, DateTime = false, Calculator = false, WebSearch = false, ReadPage = false },
            new WebSearchService(new HttpClient()), "ddg", "", "", new SearchOptions(), null, hooks);

        Assert.Equal(new[] { "remember", "forget" }, set.Tools.Select(t => t.Name));
        Assert.All(set.Tools, t => Assert.True(t.Ask));
        Assert.Equal("Saved.", await set.ExecuteAsync("remember", "{\"text\":\"Likes tea\"}", default));
        Assert.Equal(new[] { "Likes tea" }, saved);
        Assert.Equal("Deleted: tea", await set.ExecuteAsync("forget", "{\"text\":\"tea\"}", default));
    }
}
