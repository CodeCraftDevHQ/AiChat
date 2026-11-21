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
