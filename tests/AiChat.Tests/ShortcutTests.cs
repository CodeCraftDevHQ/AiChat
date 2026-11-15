using AiChat.Services;

namespace AiChat.Tests;

public class ShortcutTests
{
    [Fact]
    public void Sample_parses_without_error()
    {
        var list = ShortcutHelper.Parse(ShortcutHelper.Sample, out var err);
        Assert.Null(err);
        Assert.NotEmpty(list);
    }

    [Fact]
    public void Empty_gives_empty_list_without_error()
    {
        Assert.Empty(ShortcutHelper.Parse("", out var err));
        Assert.Null(err);
    }

    [Theory]
    [InlineData("""{"a":1}""")]
    [InlineData("""[{"name":"a b","prompt":"x"}]""")]
    [InlineData("""[{"name":"a"}]""")]
    [InlineData("""[{"name":"a","prompt":"x"},{"name":"A","prompt":"y"}]""")]
    [InlineData("[ broken")]
    public void Invalid_definitions_report_error(string json)
    {
        ShortcutHelper.Parse(json, out var err);
        Assert.False(string.IsNullOrEmpty(err));
    }

    static readonly List<Shortcut> Sc = ShortcutHelper.Parse("""
    [ {"name":"tr","prompt":"Translate:\n{{text}}"}, {"name":"hi","prompt":"Say hi."} ]
    """, out _);

    [Fact]
    public void Expands_with_text_placeholder() =>
        Assert.Equal("Translate:\nsalam", ShortcutHelper.Expand("/tr salam", Sc));

    [Fact]
    public void Expands_case_insensitive_and_multiline() =>
        Assert.Equal("Translate:\nline1\nline2", ShortcutHelper.Expand("/TR line1\nline2", Sc));

    [Fact]
    public void Appends_text_when_no_placeholder()
    {
        Assert.Equal("Say hi.", ShortcutHelper.Expand("/hi", Sc));
        Assert.Equal("Say hi.\n\nBob", ShortcutHelper.Expand("/hi Bob", Sc));
    }

    [Theory]
    [InlineData("/unknown text")]
    [InlineData("plain text")]
    [InlineData("a /tr b")]
    public void Other_text_is_unchanged(string text) =>
        Assert.Equal(text, ShortcutHelper.Expand(text, Sc));
}
