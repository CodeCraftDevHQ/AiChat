using System.Text.Json.Nodes;
using AiChat.Services;
using static AiChat.Tests.Helpers;

namespace AiChat.Tests;

public class TplTests
{
    static JsonNode? Resolve(string name) => name switch
    {
        "prompt" => "hello world",
        "n" => 42,
        "flag" => true,
        "off" => false,
        "empty" => "",
        "list" => new JsonArray(1, 2),
        _ => null
    };

    static string Run(string json) => Tpl.Expand(J(json), Resolve)!.ToJsonString();

    [Fact]
    public void Whole_string_placeholder_keeps_type()
    {
        Assert.Equal("""{"a":42,"b":true,"c":[1,2]}""", Run("""{"a":"{{n}}","b":"{{flag}}","c":"{{list}}"}"""));
    }

    [Fact]
    public void Inline_placeholder_is_text()
    {
        Assert.Equal("""{"a":"say hello world now 42"}""", Run("""{"a":"say {{prompt}} now {{n}}"}"""));
    }

    [Fact]
    public void Unset_whole_placeholder_drops_the_field()
    {
        Assert.Equal("""{"keep":1}""", Run("""{"keep":1,"gone":"{{nothing}}"}"""));
    }

    [Fact]
    public void Unset_placeholder_drops_array_item()
    {
        Assert.Equal("""[1,3]""", Run("""[1,"{{nothing}}",3]"""));
    }

    [Fact]
    public void Unset_inline_placeholder_becomes_empty_text()
    {
        Assert.Equal("""{"a":"x-y"}""", Run("""{"a":"x-{{nothing}}y"}"""));
    }

    [Theory]
    [InlineData("flag", true)]
    [InlineData("prompt", true)]
    [InlineData("n", true)]
    [InlineData("off", false)]
    [InlineData("empty", false)]
    [InlineData("nothing", false)]
    public void If_keeps_or_drops_object(string cond, bool kept)
    {
        var result = Run("""{"o":{"$if":"{{""" + cond + """}}","x":1},"z":0}""");
        Assert.Equal(kept ? """{"o":{"x":1},"z":0}""" : """{"z":0}""", result);
    }

    [Fact]
    public void Url_filter_escapes()
    {
        var r = Tpl.Expand(J("""{"q":"a/{{prompt|url}}"}"""), Resolve)!;
        Assert.Equal("a/hello%20world", r["q"]!.GetValue<string>());
    }

    [Fact]
    public void Plain_values_are_untouched()
    {
        Assert.Equal("""{"a":1,"b":null,"c":"text"}""", Run("""{"a":1,"b":null,"c":"text"}"""));
    }
}
