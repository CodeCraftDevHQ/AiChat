using AiChat.Models;
using AiChat.Services;
using static AiChat.Tests.Helpers;

namespace AiChat.Tests;

public class OpenAiStreamTests
{
    static async Task<string> Stream(string sse)
    {
        var h = new FakeHandler(_ => FakeHandler.Text(sse));
        var p = new OpenAiCompatibleProvider(new HttpClient(h));
        var cfg = new ProviderConfig { Type = ProviderType.OpenAiCompatible, BaseUrl = "http://lm.local/v1" };
        var sb = new System.Text.StringBuilder();
        await foreach (var t in p.StreamAsync(cfg, "m", User("hi"), null, null, CancellationToken.None)) sb.Append(t);
        return sb.ToString();
    }

    [Fact]
    public async Task Plain_content_is_streamed() =>
        Assert.Equal("Hello", await Stream(
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\n\ndata: [DONE]\n\n"));

    [Fact]
    public async Task Reasoning_content_is_wrapped_in_think_tags_before_the_answer()
    {
        var all = await Stream(
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"think\\nmore\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"Answer\"}}]}\n\ndata: [DONE]\n\n");
        Assert.StartsWith("<think>think\nmore</think>", all);
        Assert.EndsWith("Answer", all);
    }

    [Fact]
    public async Task Reasoning_only_answer_is_not_lost()
    {
        var all = await Stream("data: {\"choices\":[{\"delta\":{\"reasoning\":\"only thoughts\"}}]}\n\ndata: [DONE]\n\n");
        // the reasoning is also the answer (some DeepSeek versions leave the answer empty)
        Assert.Equal("<think>only thoughts</think>\n\nonly thoughts", all);
    }

    [Fact]
    public async Task Answer_labelled_as_reasoning_after_blank_content_becomes_the_answer()
    {
        var all = await Stream(
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"thought\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"\\n\\n\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"Real answer\"}}]}\n\ndata: [DONE]\n\n");
        Assert.EndsWith("Real answer", all);
        var html = MarkdownRenderer.ToHtml(all);
        Assert.Contains("thought", html);
        Assert.Equal(1, html.Split("Real answer").Length - 1);
    }
}
