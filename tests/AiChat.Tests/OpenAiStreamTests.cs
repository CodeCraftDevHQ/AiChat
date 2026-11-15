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
        Assert.Equal("<think>only thoughts</think>", all);
    }
}
