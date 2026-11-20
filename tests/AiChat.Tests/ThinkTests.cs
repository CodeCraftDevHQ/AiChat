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

    [Fact]
    public void Thinking_of_every_round_is_merged_into_one_box()
    {
        var html = MarkdownRenderer.ToHtml("<think>first</think>\n\n> 🔧 Web search: cats\n\n<think>second</think>\n\nAnswer");
        Assert.Equal(1, html.Split("<details class=\"think\"").Length - 1);
        Assert.Contains("first", html);
        Assert.Contains("second", html);
        Assert.Contains("Answer", html);
    }

    [Fact]
    public void Tool_calls_go_into_one_collapsible_box_and_are_escaped()
    {
        var html = MarkdownRenderer.ToHtml("> 🔧 Date & time\n\n> 🔧 Web search: <b>x</b>\n\nDone");
        Assert.Equal(1, html.Split("<details class=\"think toolbox\"").Length - 1);
        Assert.Equal(2, html.Split("class=\"toolcall\"").Length - 1);
        Assert.Contains("(2)</summary>", html);
        Assert.DoesNotContain("<b>x</b>", html);          // escaped
        Assert.DoesNotContain("<blockquote", html);
        Assert.Contains("Done", html);
        Assert.DoesNotContain(" open", html.Split("<summary>")[0]);   // the answer has started: closed
    }

    [Fact]
    public void Tool_calls_of_several_rounds_are_merged_and_box_is_open_until_the_answer_starts()
    {
        var running = MarkdownRenderer.ToHtml("<think>a</think>\n\n> 🔧 Web search: q1\n\n<think>b</think>\n\n> 🔧 Read page: u\n\n");
        Assert.Equal(1, running.Split("<details class=\"think toolbox\"").Length - 1);
        Assert.Contains("<details class=\"think toolbox\" open>", running);
        Assert.Contains("(2)</summary>", running);

        var done = MarkdownRenderer.ToHtml("> 🔧 Web search: q1\n\nAnswer text\n\n> 🔧 Web search: q2\n\nMore");
        Assert.Equal(1, done.Split("<details class=\"think toolbox\"").Length - 1);
        Assert.Contains("q2", done);
        Assert.DoesNotContain("<details class=\"think toolbox\" open>", done);
    }

    [Fact]
    public void Tool_lines_right_after_a_closed_think_block_are_still_grouped()
    {
        var md = "<think>a</think>> 🔧 Web search: x\n\n<think>b</think>> 🔧 Read page: https://e.com\n\nThe answer";
        var html = MarkdownRenderer.ToHtml(md);
        Assert.Equal(1, html.Split("<details class=\"think toolbox\"").Length - 1);
        Assert.DoesNotContain("<blockquote", html);
        Assert.Contains("The answer", html);
    }

    [Fact]
    public void Answer_written_only_as_reasoning_is_shown_once()
    {
        var html = MarkdownRenderer.ToHtml("<think>Hello! How can I help?</think>\n\nHello! How can I help?");
        Assert.DoesNotContain("<details", html);
        Assert.Equal(1, html.Split("How can I help").Length - 1);
        // real thinking plus a different answer keeps the box
        Assert.Contains("<details", MarkdownRenderer.ToHtml("<think>hmm</think>\n\nAnswer"));
    }
}
