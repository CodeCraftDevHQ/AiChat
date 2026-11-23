using AiChat.Models;
using AiChat.Services;
using Xunit;

namespace AiChat.Tests;

public class ChatTagsTests
{
    static Conversation C(params string[] tags) => new() { Tags = tags.ToList() };

    [Fact]
    public void Normalize_TrimsHashAndSpaces()
    {
        Assert.Equal("کار مهم", ChatTags.Normalize("  #کار    مهم "));
        Assert.Equal("", ChatTags.Normalize("  # "));
        Assert.Equal(ChatTags.MaxLength, ChatTags.Normalize(new string('a', 100)).Length);
    }

    [Fact]
    public void Add_NoDuplicatesIgnoringCase_AndLimit()
    {
        var l = new List<string>();
        Assert.True(ChatTags.Add(l, "Work"));
        Assert.False(ChatTags.Add(l, "work"));
        Assert.False(ChatTags.Add(l, " "));
        for (var i = 0; i < 20; i++) ChatTags.Add(l, "t" + i);
        Assert.Equal(ChatTags.MaxPerChat, l.Count);
    }

    [Fact]
    public void Remove_IgnoresCase()
    {
        var l = new List<string> { "Work", "Home" };
        Assert.True(ChatTags.Remove(l, "work"));
        Assert.Single(l);
        Assert.False(ChatTags.Remove(l, "x"));
    }

    [Fact]
    public void All_CountsAndSorts()
    {
        var all = ChatTags.All(new[] { C("a", "b"), C("B"), C("c", "b") });
        Assert.Equal("b", all[0].Tag);
        Assert.Equal(3, all[0].Count);
        Assert.Equal(3, all.Count);
        Assert.Equal("a", all[1].Tag);
    }

    [Fact]
    public void Matches_RequiresAllSelected()
    {
        var c = C("a", "b");
        Assert.True(ChatTags.Matches(c, new List<string>()));
        Assert.True(ChatTags.Matches(c, new List<string> { "A" }));
        Assert.True(ChatTags.Matches(c, new List<string> { "a", "b" }));
        Assert.False(ChatTags.Matches(c, new List<string> { "a", "z" }));
    }

    [Fact]
    public void Hue_IsStableAndInRange()
    {
        Assert.Equal(ChatTags.Hue("Work"), ChatTags.Hue(" work "));
        Assert.InRange(ChatTags.Hue("کار"), 0, 359);
    }

    [Fact]
    public void RenameEverywhere_MergesAndCounts()
    {
        var list = new[] { C("a", "b"), C("a"), C("b") };
        Assert.Equal(2, ChatTags.RenameEverywhere(list, "a", "b"));
        Assert.Equal(new[] { "b" }, list[0].Tags);
        Assert.Equal(new[] { "b" }, list[1].Tags);
        Assert.Equal(0, ChatTags.RenameEverywhere(list, "zz", "q"));
        Assert.Equal(0, ChatTags.RenameEverywhere(list, "b", " "));
    }

    [Fact]
    public void RemoveEverywhere_Works()
    {
        var list = new[] { C("a"), C("a", "b"), C("b") };
        Assert.Equal(2, ChatTags.RemoveEverywhere(list, "A"));
        Assert.Empty(list[0].Tags);
    }

    [Fact]
    public void Template_FromConversation_AndApply()
    {
        var src = C("x", "y");
        src.Title = "Chat"; src.PersonaId = "p1"; src.ProviderId = "pr"; src.Model = "m";
        src.Tools = true; src.Memory = false; src.ProjectId = "proj";
        src.Gen = new GenSettings { SystemPrompt = "be short" };
        var t = ChatTags.FromConversation(src, " ");
        Assert.Equal("Chat", t.Name);
        Assert.Equal("be short", t.Prompt);
        Assert.Equal(new[] { "x", "y" }, t.Tags);

        var target = new Conversation();
        ChatTags.Apply(t, target);
        Assert.Equal(new[] { "x", "y" }, target.Tags);
        Assert.True(target.Tools);
        Assert.False(target.Memory);
        Assert.Equal("be short", target.Gen!.SystemPrompt);
    }

    [Fact]
    public void Apply_EmptyPrompt_LeavesGenNull()
    {
        var c = new Conversation();
        ChatTags.Apply(new ChatTemplate { Name = "n" }, c);
        Assert.Null(c.Gen);
        Assert.Null(c.Tools);
    }

    [Fact]
    public void Json_RoundTrip_AndBadInput()
    {
        var l = new List<ChatTemplate> { new() { Name = "A", Tags = { "t" }, Tools = false }, new() { Name = " " } };
        var back = ChatTags.FromJson(ChatTags.ToJson(l));
        Assert.Single(back);
        Assert.False(back[0].Tools);
        Assert.Empty(ChatTags.FromJson("not json"));
        Assert.Empty(ChatTags.FromJson(null));
    }
}
