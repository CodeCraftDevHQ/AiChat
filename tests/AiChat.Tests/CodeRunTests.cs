using System.Text.Json.Nodes;
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

    static JsonObject Args(string code, string? lang = null)
    {
        var o = new JsonObject { ["code"] = code };
        if (lang != null) o["language"] = lang;
        return o;
    }

    static ToolSet Set(Action<ToolOptions>? tune = null)
    {
        var o = new ToolOptions { CodeRun = true };
        tune?.Invoke(o);
        return BuiltInTools.Build(o, new WebSearchService(new HttpClient()), "ddg", "", "", new SearchOptions());
    }

    [Fact]
    public void Tool_is_offered_only_when_on_and_a_language_is_enabled()
    {
        Assert.Null(BuiltInTools.Build(new ToolOptions { CodeRun = false }, new WebSearchService(new HttpClient()), "ddg", "", "", new SearchOptions()).Find("run_code"));
        Assert.NotNull(Set().Find("run_code"));
        Assert.Null(Set(o => o.CodeLanguages.ForEach(l => l.Enabled = false)).Find("run_code"));
    }

    [Fact]
    public async Task Tool_runs_javascript_by_default_and_checks_the_language()
    {
        var t = Set().Find("run_code")!;
        Assert.Contains("=> 4", await t.Run(Args("2+2"), default));
        Assert.Contains("=> 4", await t.Run(Args("2+2", "JavaScript"), default));
        Assert.Contains("unknown language", await t.Run(Args("1", "python"), default));
    }

    [Fact]
    public void Asking_is_decided_per_language()
    {
        var set = Set(o =>
        {
            o.CodeLanguages.First(l => l.Id == "javascript").Ask = false;
            var py = o.CodeLanguages.First(l => l.Id == "python");
            py.Enabled = true;
            py.Ask = true;
            CodeRunner.AllowAnyOs = true;
        });
        var t = set.Find("run_code")!;
        Assert.False(t.AskFor!(Args("1", "javascript")));
        Assert.True(t.AskFor!(Args("1", "python")));
        Assert.False(t.AskFor!(Args("1")));   // no language = the first one (JavaScript)
    }

    static CodeLanguage Sh(string args = "\"{file}\"") => new() { Id = "sh", Name = "sh", Command = "sh", Args = args, Extension = ".sh", Enabled = true };

    [Fact]
    public async Task External_program_output_stderr_and_exit_code()
    {
        if (!OperatingSystem.IsLinux()) return;
        CodeRunner.AllowAnyOs = true;
        var r = await CodeRunner.RunAsync(Sh(), "echo hello\necho oops 1>&2\nexit 3", new CodeLimits(), default);
        Assert.Contains("hello", r);
        Assert.Contains("[stderr]", r);
        Assert.Contains("oops", r);
        Assert.Contains("[exit code 3]", r);
    }

    [Fact]
    public async Task External_program_is_stopped_at_the_time_limit()
    {
        if (!OperatingSystem.IsLinux()) return;
        CodeRunner.AllowAnyOs = true;
        var l = Sh();
        l.TimeoutSec = 1;
        var r = await CodeRunner.RunAsync(l, "sleep 30", new CodeLimits(), default);
        Assert.Contains("stopped", r);
    }

    [Fact]
    public async Task External_program_not_found_is_explained()
    {
        CodeRunner.AllowAnyOs = true;
        var l = Sh();
        l.Command = "no-such-program-xyz";
        Assert.Contains("could not be started", await CodeRunner.RunAsync(l, "x", new CodeLimits(), default));
    }

    [Fact]
    public async Task Clean_environment_hides_variables()
    {
        if (!OperatingSystem.IsLinux()) return;
        CodeRunner.AllowAnyOs = true;
        Environment.SetEnvironmentVariable("AICHAT_SECRET_TEST", "topsecret");
        var clean = Sh();
        Assert.DoesNotContain("topsecret", await CodeRunner.RunAsync(clean, "echo \"v=$AICHAT_SECRET_TEST\"", new CodeLimits(), default));
        var open = Sh();
        open.CleanEnv = false;
        Assert.Contains("topsecret", await CodeRunner.RunAsync(open, "echo \"v=$AICHAT_SECRET_TEST\"", new CodeLimits(), default));
    }

    [Fact]
    public async Task External_code_is_refused_when_not_allowed()
    {
        CodeRunner.AllowAnyOs = false;
        if (OperatingSystem.IsWindows()) return;
        Assert.Contains("Windows only", await CodeRunner.RunAsync(Sh(), "echo hi", new CodeLimits(), default));
    }
}
