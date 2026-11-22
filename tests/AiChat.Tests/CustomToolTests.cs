using System.Net;
using System.Text.Json.Nodes;
using AiChat.Services;
using Xunit;

namespace AiChat.Tests;

public class CustomToolTests
{
    static JsonObject Args(string json) => (JsonObject)JsonNode.Parse(json)!;
    static readonly CodeLimits Lim = new();

    [Fact]
    public void Template_modes()
    {
        var a = Args("{\"q\":\"a b&c\",\"n\":5}");
        Assert.Equal("x=a%20b%26c", ToolTemplate.Fill("x={{q}}", a, "url"));
        Assert.Equal("x=a b&c", ToolTemplate.Fill("x={{q}}", a));
        Assert.Equal("{\"v\":\"a b&c\"}", ToolTemplate.Fill("{\"v\":{{json:q}}}", a));
        Assert.Equal("a b&c|5|", ToolTemplate.Fill("{{raw:q}}|{{ n }}|{{missing}}", a, "url").Replace("a%20b%26c", "a b&c"));
        Assert.Equal(new[] { "q", "n" }, ToolTemplate.Slots("{{q}} {{json:n}} {{q}}"));
    }

    [Fact]
    public void JsonPath_works()
    {
        var doc = JsonNode.Parse("{\"data\":{\"items\":[{\"name\":\"x\"},{\"name\":\"y\"}]},\"m\":[[1,2],[3,4]]}");
        Assert.True(JsonPathLite.TryGet(doc, "data.items[1].name", out var n));
        Assert.Equal("y", (string?)n);
        Assert.True(JsonPathLite.TryGet(doc, "m[1][0]", out var m));
        Assert.Equal(3, (int?)m);
        Assert.False(JsonPathLite.TryGet(doc, "data.nothing", out _));
        Assert.False(JsonPathLite.TryGet(doc, "data.items[9]", out _));
    }

    [Fact]
    public void Validate_finds_problems()
    {
        Assert.Contains("name", CustomToolCheck.Validate(new CustomTool { Name = "bad name", Description = "d", Code = "1" }));
        Assert.Contains("code", CustomToolCheck.Validate(new CustomTool { Name = "ok", Description = "d", Type = "js" }));
        Assert.Contains("url", CustomToolCheck.Validate(new CustomTool { Name = "ok", Description = "d", Type = "http", Url = "ftp://x" }));
        Assert.Empty(CustomToolCheck.Validate(new CustomTool { Name = "ok", Description = "d", Type = "http", Url = "https://x.com/{{q}}" }));
        Assert.Contains("param-duplicate", CustomToolCheck.Validate(new CustomTool
        { Name = "ok", Description = "d", Code = "1", Params = { new() { Name = "a" }, new() { Name = "a" } } }));
    }

    [Theory]
    [InlineData("my tool!", "my_tool")]
    [InlineData("1abc", "tool_1abc")]
    [InlineData("", "tool_")]
    public void Sanitize_names(string input, string expected) => Assert.Equal(expected, CustomToolCheck.Sanitize(input));

    [Fact]
    public void Schema_has_types_and_required()
    {
        var t = new CustomTool { Params = { new() { Name = "a", Type = "number", Description = "A" }, new() { Name = "b", Required = false } } };
        var s = CustomToolBuilder.Schema(t);
        Assert.Equal("number", (string?)s["properties"]!["a"]!["type"]);
        Assert.Equal("string", (string?)s["properties"]!["b"]!["type"]);
        Assert.Single((JsonArray)s["required"]!);
    }

    [Fact]
    public async Task Js_tool_gets_args()
    {
        var t = new CustomTool { Name = "sum", Description = "d", Type = "js", Code = "console.log(args.a + args.b)" };
        var r = await CustomToolRunner.RunAsync(t, Args("{\"a\":2,\"b\":3}"), Lim, new HttpClient(), CancellationToken.None);
        Assert.Contains("5", r);
    }

    [Fact]
    public async Task Prompt_tool_fills_template()
    {
        var t = new CustomTool { Name = "p", Description = "d", Type = "prompt", Template = "Notes: {{notes}}" };
        Assert.Equal("Notes: hello", await CustomToolRunner.RunAsync(t, Args("{\"notes\":\"hello\"}"), Lim, new HttpClient(), CancellationToken.None));
    }

    [Fact]
    public async Task Http_tool_builds_request_and_picks_path()
    {
        var h = new FakeHandler(_ => FakeHandler.Json("{\"data\":{\"temp\":21}}"));
        var t = new CustomTool
        {
            Name = "w", Description = "d", Type = "http", Method = "POST", Url = "https://api.test/w?city={{city}}",
            Headers = "X-Key: k-{{city}}", Body = "{\"c\":{{json:city}}}", ResponsePath = "data.temp"
        };
        var r = await CustomToolRunner.RunAsync(t, Args("{\"city\":\"New York\"}"), Lim, new HttpClient(h), CancellationToken.None);
        Assert.Equal("21", r);
        var seen = h.Requests.Single();
        Assert.Equal("https://api.test/w?city=New%20York", seen.Url);
        Assert.Equal("k-New York", seen.Headers["X-Key"]);
        Assert.Equal("{\"c\":\"New York\"}", seen.Body);
        Assert.StartsWith("application/json", seen.ContentType);
    }

    [Fact]
    public async Task Http_tool_reports_errors_and_missing_path()
    {
        var bad = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("nope") });
        var t = new CustomTool { Name = "w", Description = "d", Type = "http", Url = "https://api.test/x" };
        var r = await CustomToolRunner.RunAsync(t, new JsonObject(), Lim, new HttpClient(bad), CancellationToken.None);
        Assert.StartsWith("Error: HTTP 404", r);

        var ok = new FakeHandler(_ => FakeHandler.Json("{\"a\":1}"));
        t.ResponsePath = "b.c";
        r = await CustomToolRunner.RunAsync(t, new JsonObject(), Lim, new HttpClient(ok), CancellationToken.None);
        Assert.StartsWith("Error: 'b.c' was not found", r);
    }

    [Fact]
    public void Command_line_fills_one_argument_per_line()
    {
        var t = new CustomTool { Command = " ping ", Args = "-n\n2\n{{host}}\n\n" };
        var (prog, args) = CustomToolRunner.CommandLine(t, Args("{\"host\":\"a b; rm -rf\"}"));
        Assert.Equal("ping", prog);
        Assert.Equal(new[] { "-n", "2", "a b; rm -rf" }, args);   // a value can never become several arguments
    }

    [Fact]
    public void BuildAll_skips_disabled_invalid_and_renames_duplicates()
    {
        var list = new[]
        {
            new CustomTool { Name = "calc", Description = "d", Type = "prompt", Template = "x" },
            new CustomTool { Name = "off", Description = "d", Type = "prompt", Template = "x", Enabled = false },
            new CustomTool { Name = "broken", Description = "", Type = "prompt", Template = "x" },
            new CustomTool { Name = "mine", Description = "d", Type = "prompt", Template = "x", Ask = false, Label = "My" },
            new CustomTool { Name = "mine", Description = "d", Type = "prompt", Template = "y" },
        };
        var tools = CustomToolBuilder.BuildAll(list, Lim, new HttpClient(), new HashSet<string> { "calc" });
        Assert.Equal(new[] { "calc_2", "mine", "mine_2" }, tools.Select(t => t.Name));
        Assert.All(tools, t => Assert.Equal("custom", t.Source));
        Assert.Equal("My", tools[1].Label);
        Assert.False(tools[1].Ask);
    }

    [Fact]
    public void Export_import_round_trip_and_forms()
    {
        var src = CustomToolIo.Examples();
        var (back, err) = CustomToolIo.Import(CustomToolIo.Export(src));
        Assert.Null(err);
        Assert.Equal(src.Select(t => t.Name), back.Select(t => t.Name));
        Assert.All(back, t => Assert.Empty(CustomToolCheck.Validate(t)));
        Assert.NotEqual(src[0].Id, back[0].Id);

        Assert.Null(CustomToolIo.Import("[{\"name\":\"a\",\"description\":\"d\",\"type\":\"prompt\",\"template\":\"t\"}]").Error);
        Assert.Null(CustomToolIo.Import("{\"name\":\"a\",\"description\":\"d\",\"type\":\"nonsense\",\"code\":\"1\"}").Error);
        Assert.NotNull(CustomToolIo.Import("not json").Error);
        Assert.NotNull(CustomToolIo.Import("[]").Error);
    }

    [Fact]
    public void Examples_are_valid()
    {
        foreach (var t in CustomToolIo.Examples()) Assert.Empty(CustomToolCheck.Validate(t));
    }

    [Fact]
    public void Preview_shows_request_and_command()
    {
        var h = new CustomTool { Type = "http", Method = "get", Url = "https://a.b/{{q}}" };
        Assert.StartsWith("GET https://a.b/x%20y", CustomToolBuilder.Preview(h, Args("{\"q\":\"x y\"}")));
        var c = new CustomTool { Type = "command", Command = "ping", Args = "-n\n{{h}}" };
        Assert.StartsWith("ping -n \"a b\"", CustomToolBuilder.Preview(c, Args("{\"h\":\"a b\"}")));
    }
}
