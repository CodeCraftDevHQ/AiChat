using System.Text.Json.Nodes;
using AiChat.Models;
using AiChat.Services;
using static AiChat.Tests.Helpers;

namespace AiChat.Tests;

public class ToolTests
{
    [Theory]
    [InlineData("1+2*3", "7")]
    [InlineData("(1+2)*3", "9")]
    [InlineData("2^3^2", "512")]
    [InlineData("-2^2", "-4")]
    [InlineData("10/4", "2.5")]
    [InlineData("10%3", "1")]
    [InlineData("sqrt(16)+abs(-2)", "6")]
    [InlineData("max(1,5,3)", "5")]
    [InlineData("round(1.2345,2)", "1.23")]
    [InlineData("round(2.5)", "3")]
    [InlineData("1e3+1", "1001")]
    [InlineData("3×4", "12")]
    public void Calculator_evaluates(string expr, string expected) =>
        Assert.Equal(expected, MathEval.Format(MathEval.Eval(expr)));

    [Theory]
    [InlineData("1+")]
    [InlineData("foo(2)")]
    [InlineData("2 3")]
    [InlineData("(1+2")]
    public void Calculator_rejects_bad_input(string expr) =>
        Assert.ThrowsAny<Exception>(() => MathEval.Eval(expr));

    [Fact]
    public void Division_by_zero_is_reported_not_thrown() =>
        Assert.StartsWith("Error", MathEval.Format(MathEval.Eval("1/0")));

    [Fact]
    public void Tool_call_lines_are_removed_from_history()
    {
        var text = "Before\n\n> 🔧 Web search: cats\n\nAfter";
        Assert.Equal("Before\n\nAfter", ToolMarks.Strip(text));
        Assert.Equal("plain", ToolMarks.Strip("plain"));
    }

    static ToolSet Echo(int maxRounds = 5) => new()
    {
        MaxRounds = maxRounds,
        Tools =
        {
            new ToolDef
            {
                Name = "echo", Label = "Echo", Description = "d",
                Run = (a, _) => Task.FromResult("ECHO:" + a["text"])
            }
        }
    };

    [Fact]
    public async Task Execute_unknown_tool_and_errors_become_text()
    {
        var set = Echo();
        Assert.StartsWith("Error: unknown tool", await set.ExecuteAsync("nope", "{}", default));
        set.Tools.Add(new ToolDef { Name = "boom", Run = (_, _) => throw new InvalidOperationException("bad") });
        Assert.Equal("Error: bad", await set.ExecuteAsync("boom", "{}", default));
    }

    [Fact]
    public async Task Refused_call_is_not_run_and_long_results_are_cut()
    {
        var set = Echo();
        set.Approve = (_, _) => Task.FromResult(false);
        Assert.Contains("did not allow", await set.ExecuteAsync("echo", "{\"text\":\"x\"}", default));

        set.Approve = null;
        set.ResultMaxChars = 500;
        set.Tools.Add(new ToolDef { Name = "big", Run = (_, _) => Task.FromResult(new string('a', 2000)) });
        var r = await set.ExecuteAsync("big", "{}", default);
        Assert.EndsWith("[...cut]", r);
        Assert.True(r.Length < 600);
    }

    [Fact]
    public void Call_line_shows_label_and_first_argument()
    {
        var line = Echo().CallLine("echo", "{\"text\":\"hello world\"}");
        Assert.Equal("> 🔧 Echo: hello world\n\n", line);
    }

    static string ToolCallSse(string argsJson) =>
        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"echo\",\"arguments\":\"\"}}]}}]}\n\n" +
        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":" + JsonValue.Create(argsJson)!.ToJsonString() + "}}]}}]}\n\n" +
        "data: [DONE]\n\n";

    [Fact]
    public async Task Model_calls_a_tool_and_continues_with_the_result()
    {
        var n = 0;
        var h = new FakeHandler(_ => n++ == 0
            ? FakeHandler.Text(ToolCallSse("{\"text\":\"hi\"}"))
            : FakeHandler.Text("data: {\"choices\":[{\"delta\":{\"content\":\"Done\"}}]}\n\ndata: [DONE]\n\n"));
        var p = new OpenAiCompatibleProvider(new HttpClient(h));
        var cfg = new ProviderConfig { Type = ProviderType.OpenAiCompatible, BaseUrl = "http://lm.local/v1", Tools = Echo() };

        var sb = new System.Text.StringBuilder();
        await foreach (var t in p.StreamAsync(cfg, "m", User("go"), null, null, CancellationToken.None))
            if (!t.StartsWith(ProviderStatus.Prefix, StringComparison.Ordinal)) sb.Append(t);

        Assert.Contains("> 🔧 Echo: hi", sb.ToString());
        Assert.EndsWith("Done", sb.ToString());
        Assert.Equal(2, h.Requests.Count);
        Assert.Contains("\"tools\"", h.Requests[0].Body);
        Assert.Contains("\"role\":\"tool\"", h.Requests[1].Body);
        Assert.Contains("ECHO:hi", h.Requests[1].Body);
        Assert.Contains("\"tool_call_id\":\"c1\"", h.Requests[1].Body);
    }

    [Fact]
    public async Task After_the_last_round_no_tools_are_offered()
    {
        var h = new FakeHandler(_ => FakeHandler.Text(ToolCallSse("{\"text\":\"again\"}")));
        var p = new OpenAiCompatibleProvider(new HttpClient(h));
        var cfg = new ProviderConfig { Type = ProviderType.OpenAiCompatible, BaseUrl = "http://lm.local/v1", Tools = Echo(maxRounds: 2) };

        await foreach (var _ in p.StreamAsync(cfg, "m", User("go"), null, null, CancellationToken.None)) { }

        Assert.Equal(3, h.Requests.Count);                       // 2 rounds with tools + 1 final request without
        Assert.Contains("\"tools\"", h.Requests[1].Body);
        Assert.DoesNotContain("\"tools\"", h.Requests[2].Body);
    }

    [Fact]
    public async Task Without_tools_nothing_changes()
    {
        var h = new FakeHandler(_ => FakeHandler.Text("data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\ndata: [DONE]\n\n"));
        var p = new OpenAiCompatibleProvider(new HttpClient(h));
        var cfg = new ProviderConfig { Type = ProviderType.OpenAiCompatible, BaseUrl = "http://lm.local/v1" };
        var sb = new System.Text.StringBuilder();
        await foreach (var t in p.StreamAsync(cfg, "m", User("x"), null, null, CancellationToken.None)) sb.Append(t);
        Assert.Equal("Hi", sb.ToString());
        Assert.DoesNotContain("\"tools\"", h.Requests[0].Body);
    }
}
