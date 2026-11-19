using AiChat.Services;

namespace AiChat.Tests;

public class SummaryTests
{
    static List<string> Roles(int n) => Enumerable.Range(0, n).Select(i => i % 2 == 0 ? "user" : "assistant").ToList();

    [Fact]
    public void NextCut_KeepsTheNewestMessages_AndStartsOnAUserMessage()
    {
        var roles = Roles(20);                       // user, assistant, user, ...
        var cut = SummaryHelper.NextCut(roles, 0, 6);
        Assert.Equal(14, cut);                       // 20 - 6 = 14, which is a user message
        Assert.Equal("user", roles[cut]);

        // keepLast 5 would cut at 15 (an assistant message): move back to 14
        Assert.Equal(14, SummaryHelper.NextCut(roles, 0, 5));
    }

    [Fact]
    public void NextCut_ContinuesAfterAnEarlierSummary()
    {
        var roles = Roles(30);
        Assert.Equal(24, SummaryHelper.NextCut(roles, 14, 6));
    }

    [Fact]
    public void NextCut_DoesNothingWhenThereIsLittleToSummarize()
    {
        Assert.Equal(0, SummaryHelper.NextCut(Roles(6), 0, 6));
        Assert.Equal(0, SummaryHelper.NextCut(Roles(7), 0, 6));      // only one message before the kept part
        Assert.Equal(14, SummaryHelper.NextCut(Roles(20), 14, 6));   // already summarized up to here
        Assert.Equal(0, SummaryHelper.NextCut(Roles(0), 0, 6));
    }

    [Fact]
    public void NextCut_AlwaysKeepsAtLeastTwoMessages()
    {
        var cut = SummaryHelper.NextCut(Roles(10), 0, 0);
        Assert.True(10 - cut >= 2);
    }

    [Fact]
    public void IsTooLong_CountsSummaryAndMessages()
    {
        Assert.False(SummaryHelper.IsTooLong(null, new[] { new string('a', 300) }, 1000));
        Assert.True(SummaryHelper.IsTooLong(new string('s', 1500), new[] { new string('a', 1600) }, 1000));
        Assert.True(SummaryHelper.IsTooLong(null, new[] { new string('a', 6000) }, 1000));
        Assert.False(SummaryHelper.IsTooLong(null, new[] { new string('a', 900) }, 10));   // the limit is never below 500
    }

    [Fact]
    public void Prompt_ContainsSummaryMessagesAndLimit_AndCustomInstructionsReplaceTheDefault()
    {
        var p = SummaryHelper.BuildPrompt("old summary", "User: hi", 120, null);
        Assert.Contains(SummaryHelper.DefaultInstructions, p);
        Assert.Contains("at most 120 words", p);
        Assert.Contains("old summary", p);
        Assert.Contains("User: hi", p);

        var c = SummaryHelper.BuildPrompt(null, "x", 120, "Summarize in Persian");
        Assert.StartsWith("Summarize in Persian", c);
        Assert.DoesNotContain(SummaryHelper.DefaultInstructions, c);
        Assert.Contains("(none yet)", c);
    }

    [Fact]
    public void MessageText_And_SystemNote()
    {
        Assert.Equal("User: hello [attachments: a.png, b.pdf]", SummaryHelper.MessageText("user", " hello ", new[] { "a.png", "b.pdf" }));
        Assert.Equal("Assistant: ok", SummaryHelper.MessageText("assistant", "ok"));
        Assert.Equal("", SummaryHelper.SystemNote("  "));
        Assert.Contains("not repeated", SummaryHelper.SystemNote("S"));
        Assert.EndsWith("S", SummaryHelper.SystemNote("S"));
    }

    [Fact]
    public void EstimateTokens_IsRoughlyThirdOfTheLength()
    {
        Assert.Equal(0, SummaryHelper.EstimateTokens(null));
        Assert.Equal(0, SummaryHelper.EstimateTokens(""));
        Assert.Equal(100, SummaryHelper.EstimateTokens(new string('x', 300)));
    }
}
