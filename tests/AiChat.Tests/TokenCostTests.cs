using AiChat.Models;
using AiChat.Services;

namespace AiChat.Tests;

public class TokenCostTests
{
    static readonly CostOptions O = new();

    static ChatMessage Msg(string role, string text, string model = "", params Attachment[] atts)
    {
        var m = new ChatMessage { Role = role, Content = text, Model = model };
        m.Attachments.AddRange(atts);
        return m;
    }

    [Fact]
    public void Latin_and_persian_text_use_their_own_ratio()
    {
        Assert.Equal(0, TokenCost.Estimate("", O));
        Assert.Equal(3, TokenCost.Estimate(new string('a', 12), O));      // 12 / 4
        Assert.Equal(5, TokenCost.Estimate(new string('س', 10), new CostOptions { ArabicCharsPerToken = 2 }));      // 10 / 2
        Assert.Equal(10, TokenCost.Estimate("你好世界你好世界你好", O));   // 1 per character
    }

    [Fact]
    public void Attachments_are_counted_by_kind()
    {
        Assert.Equal(800, TokenCost.ForAttachment(new Attachment { Kind = "image" }, O));
        Assert.Equal(250, TokenCost.ForAttachment(new Attachment { Kind = "text", Size = 1000 }, O));
        Assert.Equal(0, TokenCost.ForAttachment(new Attachment { Kind = "video", Size = 5_000_000 }, O));
        Assert.Equal(0, TokenCost.ForAttachment(new Attachment { Kind = "image", Excluded = true }, O));
    }

    [Fact]
    public void Price_is_found_by_part_of_the_model_name_with_default_row_last()
    {
        var o = new CostOptions
        {
            Prices =
            {
                new PriceRow { Pattern = "", InputPerM = 9, OutputPerM = 9 },
                new PriceRow { Pattern = "FLASH", InputPerM = 1, OutputPerM = 2, ContextK = 128 },
            }
        };
        Assert.Equal(1, TokenCost.FindPrice(o, "Google Gemini · gemini-flash-lite")!.InputPerM);
        Assert.Equal(9, TokenCost.FindPrice(o, "other-model")!.InputPerM);
        Assert.Null(TokenCost.FindPrice(new CostOptions(), "x"));
    }

    [Fact]
    public void History_is_resent_so_cost_grows_with_every_answer()
    {
        var o = new CostOptions { Prices = { new PriceRow { Pattern = "m", InputPerM = 1_000_000, OutputPerM = 2_000_000, ContextK = 1 } } };
        var u1 = Msg("user", new string('a', 40));          // 10 + 4 = 14 tokens
        var a1 = Msg("assistant", new string('b', 20), "m"); // 5 + 4 = 9
        var u2 = Msg("user", new string('c', 8));           // 2 + 4 = 6
        var a2 = Msg("assistant", new string('d', 4), "m");  // 1 + 4 = 5
        var st = TokenCost.Analyze(new[] { u1, a1, u2, a2 }, baseTokens: 100, currentModel: "m", o);

        Assert.Equal(114, st.PerMessage[a1].InputTokens);               // base + u1
        Assert.Equal(9, st.PerMessage[a1].Tokens);
        Assert.Equal(100 + 14 + 9 + 6, st.PerMessage[a2].InputTokens);  // everything before a2
        Assert.Equal(100 + 14 + 9 + 6 + 5, st.ContextTokens);
        Assert.Equal(114 + 129, st.TotalInput);
        Assert.Equal(9 + 5, st.TotalOutput);
        Assert.Equal(1000, st.ContextLimit);
        // 1 token = 1 currency unit for input, 2 for output
        Assert.Equal(243 * 1.0 + 14 * 2.0, st.Cost!.Value, 6);
    }

    [Fact]
    public void Without_a_price_only_tokens_are_reported()
    {
        var st = TokenCost.Analyze(new[] { Msg("user", "hi"), Msg("assistant", "yo") }, 0, "m", O);
        Assert.Null(st.Cost);
        Assert.Null(st.ContextLimit);
        Assert.True(st.ContextTokens > 0);
    }

    [Fact]
    public void Failed_empty_answers_are_not_billed()
    {
        var st = TokenCost.Analyze(new[] { Msg("user", "hi"), Msg("assistant", "") }, 0, "m",
            new CostOptions { Prices = { new PriceRow { InputPerM = 1, OutputPerM = 1 } } });
        Assert.Null(st.Cost);
    }

    [Theory]
    [InlineData(950, "950")]
    [InlineData(1500, "1.5k")]
    [InlineData(250_000, "250k")]
    public void Tokens_are_formatted_short(int n, string expected) => Assert.Equal(expected, TokenCost.FormatTokens(n));

    [Theory]
    [InlineData(12.5, "$12.50")]
    [InlineData(0.0456, "$0.046")]
    [InlineData(0.00123, "$0.0012")]
    public void Money_is_formatted_by_size(double v, string expected) => Assert.Equal(expected, TokenCost.FormatMoney(v, "$"));
}
