using AiChat.Services;

namespace AiChat.Tests;

public class ThinkTests
{
    [Fact]
    public void Closed_think_block_becomes_a_collapsed_details_box()
    {
        var html = MarkdownRenderer.ToHtml("<think>step one\n\nstep two</think>\n\nThe **answer**");
        Assert.Contains("<details class=\"think\"><summary>", html);
        Assert.DoesNotContain(" open", html.Split("<summary>")[0]);
        Assert.Contains("step two", html);
        Assert.Contains("<strong>answer</strong>", html);
        Assert.True(html.IndexOf("step one") < html.IndexOf("answer"));
    }

    [Fact]
    public void Unclosed_think_block_is_open_while_streaming()
    {
        var html = MarkdownRenderer.ToHtml("<think>still thinking");
        Assert.Contains("<details class=\"think\" open>", html);
        Assert.Contains("still thinking", html);
    }

    [Fact]
    public void Html_inside_thinking_is_still_escaped()
    {
        var html = MarkdownRenderer.ToHtml("<think><script>alert(1)</script></think>x");
        Assert.DoesNotContain("<script>", html);
    }

    [Fact]
    public void Text_without_think_is_unchanged_markdown() =>
        Assert.Contains("<strong>a</strong>", MarkdownRenderer.ToHtml("**a**"));

    [Theory]
    [InlineData("<think>x</think>\n\nAnswer", "Answer")]
    [InlineData("<think>unfinished", "")]
    [InlineData("plain", "plain")]
    [InlineData("A<think>x</think>B", "AB")]
    public void StripThink_removes_thinking(string input, string expected) =>
        Assert.Equal(expected, MarkdownRenderer.StripThink(input));

    [Fact]
    public void Think_display_options_are_applied()
    {
        try
        {
            MarkdownRenderer.SetThinkOptions("open", 150);
            var open = MarkdownRenderer.ToHtml("<think>a</think>b");
            Assert.Contains("<details class=\"think\" open>", open);
            Assert.Contains("max-height:150px", open);

            MarkdownRenderer.SetThinkOptions("closed", 150);
            Assert.DoesNotContain(" open>", MarkdownRenderer.ToHtml("<think>still</think>b").Split("<summary>")[0]);

            MarkdownRenderer.SetThinkOptions("hidden", 150);
            var hidden = MarkdownRenderer.ToHtml("<think>secret thoughts</think>answer");
            Assert.DoesNotContain("secret thoughts", hidden);
            Assert.Contains("answer", hidden);
        }
        finally { MarkdownRenderer.SetThinkOptions("auto", 320); }
    }
}
