using AiChat.Services;

namespace AiChat.Tests;

public class CodeRunTests
{
    static Task<string> Run(string code, CodeLimits? l = null) => CodeRunner.RunAsync(code, l ?? new CodeLimits { TimeoutSec = 3 }, default);

    [Fact]
    public async Task Prints_and_returns_last_value()
    {
        var r = await Run("console.log('hi', 2+3); [1,2,3].map(x => x*2)");
        Assert.Contains("hi 5", r);
        Assert.Contains("=> [2,4,6]", r);
    }

    [Fact]
    public async Task Objects_are_printed_as_json()
    {
        Assert.Contains("{\"a\":1}", await Run("console.log({a:1})"));
    }

    [Fact]
    public async Task Syntax_and_runtime_errors_become_text()
    {
        Assert.StartsWith("Error", await Run("let ="));
        Assert.StartsWith("Error", await Run("throw new Error('boom')"));
        Assert.Contains("boom", await Run("throw new Error('boom')"));
    }

    [Fact]
    public async Task Endless_loop_is_stopped()
    {
        var r = await Run("while(true){}", new CodeLimits { TimeoutSec = 1, MaxStatements = 0 });
        Assert.StartsWith("Error", r);
    }

    [Fact]
    public async Task Statement_limit_stops_the_code()
    {
        var r = await Run("var i=0; while(true){i++}", new CodeLimits { TimeoutSec = 10, MaxStatements = 10000 });
        Assert.Contains("too many steps", r);
    }

    [Fact]
    public async Task Output_is_cut()
    {
        var r = await Run("for(var i=0;i<1000;i++) console.log('line number '+i)", new CodeLimits { OutputChars = 300 });
        Assert.Contains("[...output cut]", r);
        Assert.True(r.Length < 400);
    }

    [Fact]
    public async Task No_access_to_dotnet_or_files()
    {
        Assert.StartsWith("Error", await Run("System.IO.File.ReadAllText('x')"));
        Assert.StartsWith("Error", await Run("require('fs')"));
    }

    [Fact]
    public async Task Empty_code_and_no_output()
    {
        Assert.StartsWith("Error", await Run("  "));
        Assert.Contains("no output", await Run("var a = 1;"));
    }

    [Fact]
    public void Tool_is_offered_only_when_on_and_asks_by_default()
    {
        var off = BuiltInTools.Build(new ToolOptions { CodeRun = false }, new WebSearchService(new HttpClient()), "ddg", "", "", new SearchOptions());
        Assert.Null(off.Find("run_javascript"));
        var on = BuiltInTools.Build(new ToolOptions { CodeRun = true }, new WebSearchService(new HttpClient()), "ddg", "", "", new SearchOptions());
        Assert.True(on.Find("run_javascript")!.Ask);
        var noAsk = BuiltInTools.Build(new ToolOptions { CodeRun = true, CodeAsk = false }, new WebSearchService(new HttpClient()), "ddg", "", "", new SearchOptions());
        Assert.False(noAsk.Find("run_javascript")!.Ask);
    }
}
