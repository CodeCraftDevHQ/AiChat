using AiChat.Models;
using AiChat.Services;
using Xunit;

namespace AiChat.Tests;

public class CompareTests
{
    [Fact]
    public void History_appends_prompt_and_skips_empty()
    {
        var chat = new[]
        {
            new ChatMessage { Role = "user", Content = "hi" },
            new ChatMessage { Role = "assistant", Content = "" },
            new ChatMessage { Role = "assistant", Content = "hello" },
        };
        var h = CompareHelper.BuildHistory(chat, "next");
        Assert.Equal(3, h.Count);
        Assert.Equal("next", h[^1].Content);
        Assert.Equal("user", h[^1].Role);
    }

    [Fact]
    public void History_without_chat()
    {
        var h = CompareHelper.BuildHistory(null, "q");
        Assert.Single(h);
    }

    [Theory]
    [InlineData(3.24, "3.2 s")]
    [InlineData(65, "1:05")]
    public void FormatTime_works(double sec, string expected) =>
        Assert.Equal(expected, CompareHelper.FormatTime(TimeSpan.FromSeconds(sec)).Replace(",", "."));

    [Fact]
    public void Cost_uses_price_table()
    {
        var o = new CostOptions();
        o.Prices.Add(new PriceRow { Pattern = "gpt", InputPerM = 2, OutputPerM = 10 });
        Assert.Equal(0.012, CompareHelper.Cost(o, "my-gpt-5", 1000, 1000)!.Value, 6);
        Assert.Null(CompareHelper.Cost(o, "claude", 1000, 1000));
    }

    [Fact]
    public void TokensPerSecond_handles_zero()
    {
        Assert.Equal(0, CompareHelper.TokensPerSecond(100, TimeSpan.Zero));
        Assert.Equal(50, CompareHelper.TokensPerSecond(100, TimeSpan.FromSeconds(2)), 3);
    }
}

public class ArenaTests
{
    static ChatMessage A(string text, string b) => new() { Role = "assistant", Content = text, Model = "A", Pair = new ChatMessage { Role = "assistant", Content = b, Model = "B" } };

    [Fact]
    public void ArenaHistory_uses_pair_answers()
    {
        var msgs = new[] { new ChatMessage { Role = "user", Content = "q" }, A("a1", "b1"), new ChatMessage { Role = "user", Content = "q2" } };
        var h = CompareHelper.ArenaHistory(msgs);
        Assert.Equal(new[] { "q", "b1", "q2" }, h.Select(m => m.Content));
    }

    [Fact]
    public void ArenaHistory_skips_empty_pair_answer()
    {
        var msgs = new[] { new ChatMessage { Role = "user", Content = "q" }, A("a1", ""), new ChatMessage { Role = "user", Content = "q2" } };
        Assert.Equal(2, CompareHelper.ArenaHistory(msgs).Count);
    }

    [Fact]
    public void KeepSide_B_replaces_and_clears()
    {
        var m = A("a1", "b1");
        m.Vote = "a";
        CompareHelper.KeepSide(new[] { m }, true);
        Assert.Equal("b1", m.Content);
        Assert.Equal("B", m.Model);
        Assert.Null(m.Pair);
        Assert.Null(m.Vote);
    }

    [Fact]
    public void KeepSide_A_keeps_main()
    {
        var m = A("a1", "b1");
        CompareHelper.KeepSide(new[] { m }, false);
        Assert.Equal("a1", m.Content);
        Assert.Null(m.Pair);
    }

    [Fact]
    public void Pair_survives_json()
    {
        var m = A("a1", "b1"); m.Vote = "both";
        var back = System.Text.Json.JsonSerializer.Deserialize<ChatMessage>(System.Text.Json.JsonSerializer.Serialize(m))!;
        Assert.Equal("b1", back.Pair!.Content);
        Assert.Equal("both", back.Vote);
    }
}
