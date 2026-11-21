using System.Text.Json.Nodes;
using AiChat.Services;
using Xunit;

namespace AiChat.Tests;

public class VoiceTests
{
    [Fact]
    public void Off_by_default()
    {
        var o = new VoiceOptions();
        Assert.False(o.Enabled);
        Assert.False(o.AutoRead);
        Assert.False(o.SttEnabled);
    }

    [Fact]
    public void Clean_removes_markdown_code_and_thinking()
    {
        var md = "<think>secret</think>\n\n# Title\n\n**Bold** and `code` text with [a link](https://x.com).\n\n```python\nprint(1)\n```\n\n- item one\n1. item two\n\n> 🔧 web_search(\"x\")";
        var t = SpeechText.Clean(md, new VoiceOptions());
        Assert.DoesNotContain("secret", t);
        Assert.DoesNotContain("print", t);
        Assert.DoesNotContain("web_search", t);
        Assert.DoesNotContain("https", t);
        Assert.DoesNotContain("*", t);
        Assert.Contains("Title", t);
        Assert.Contains("Bold and code text with a link.", t);
        Assert.Contains("item one", t);
        Assert.DoesNotContain("1.", t);
    }

    [Fact]
    public void Clean_can_keep_code_links_and_thinking()
    {
        var o = new VoiceOptions { SkipCode = false, SkipLinks = false, SkipThinking = false };
        var t = SpeechText.Clean("<think>idea</think>see [x](https://a.b)\n```\ncode here\n```", o);
        Assert.Contains("idea", t);
        Assert.Contains("code here", t.Replace("`", ""));
        Assert.Contains("https://a.b", t);
    }

    [Fact]
    public void Chunks_respect_the_limit_and_keep_all_text()
    {
        var text = string.Join(" ", Enumerable.Range(1, 200).Select(i => $"Sentence number {i}."));
        var chunks = SpeechText.Chunks(text, 300);
        Assert.All(chunks, c => Assert.True(c.Length <= 300));
        Assert.Equal(text.Replace(" ", ""), string.Concat(chunks).Replace(" ", ""));
        Assert.Single(SpeechText.Chunks("short", 300));
    }

    [Fact]
    public void Chunks_cut_long_words_without_spaces()
    {
        var chunks = SpeechText.Chunks(new string('a', 1000), 400);
        Assert.Equal(3, chunks.Count);
    }

    [Fact]
    public void GuessLanguage_works()
    {
        Assert.Equal("fa", SpeechText.GuessLanguage("سلام، حال شما چطور است؟ hello"));
        Assert.Equal("en", SpeechText.GuessLanguage("Hello there, how are you?"));
    }

    [Fact]
    public void Speech_body_has_fields_and_extra_body_overrides()
    {
        var o = new VoiceOptions { TtsModel = "m", TtsVoice = "v", TtsSpeed = 1.25, TtsExtraBody = "{\"voice\":\"other\",\"model\":null,\"lang\":\"fa\"}" };
        var b = VoiceApi.BuildSpeechBody(o, "hi");
        Assert.Equal("hi", (string?)b["input"]);
        Assert.Equal("other", (string?)b["voice"]);
        Assert.Null(b["model"]);
        Assert.Equal("fa", (string?)b["lang"]);
        Assert.Equal(1.25, (double?)b["speed"]);
        Assert.Equal("mp3", (string?)b["response_format"]);
    }

    [Fact]
    public void Broken_extra_body_is_ignored()
    {
        var b = VoiceApi.BuildSpeechBody(new VoiceOptions { TtsExtraBody = "{oops" }, "x");
        Assert.Equal("x", (string?)b["input"]);
    }

    [Fact]
    public void Join_and_mime()
    {
        Assert.Equal("https://a.com/v1/audio/speech", VoiceApi.Join("https://a.com/v1/", "/audio/speech"));
        Assert.Equal("audio/wav", VoiceApi.MimeFor("WAV"));
        Assert.Equal("audio/mpeg", VoiceApi.MimeFor("mp3"));
        Assert.Equal("audio.webm", VoiceApi.FileNameFor("audio/webm;codecs=opus"));
        Assert.Equal("audio.mp4", VoiceApi.FileNameFor("audio/mp4"));
    }

    [Theory]
    [InlineData("{\"text\":\" سلام \"}", "سلام")]
    [InlineData("plain text", "plain text")]
    [InlineData("{\"foo\":1}", "")]
    public void ParseTranscript_works(string input, string expected) =>
        Assert.Equal(expected, VoiceApi.ParseTranscript(input));

    [Fact]
    public void ParseTranscript_throws_on_error_object()
    {
        Assert.Throws<InvalidOperationException>(() => VoiceApi.ParseTranscript("{\"error\":{\"message\":\"bad\"}}"));
    }
}
