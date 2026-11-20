using System.Text;
using Jint;

namespace AiChat.Services;

/// <summary>Limits of the code-execution tool (all adjustable in the settings).</summary>
public class CodeLimits
{
    public int TimeoutSec { get; set; } = 10;
    public int MemoryMb { get; set; } = 16;
    /// <summary>Longest output returned to the model (characters).</summary>
    public int OutputChars { get; set; } = 6000;
    /// <summary>Most statements one run may execute (0 = no limit besides the time).</summary>
    public int MaxStatements { get; set; } = 5_000_000;
    public int MaxRecursion { get; set; } = 300;
}

/// <summary>
/// Runs JavaScript with Jint, a JavaScript interpreter written in .NET. There is no browser, no Node.js,
/// no files, no network and no access to .NET types: the code can only compute and print.
/// </summary>
public static class CodeRunner
{
    public static async Task<string> RunAsync(string code, CodeLimits lim, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code)) return "Error: empty code.";
        var timeout = TimeSpan.FromSeconds(Math.Clamp(lim.TimeoutSec, 1, 120));
        var cap = Math.Clamp(lim.OutputChars, 200, 50000);
        // Runs on its own thread so a stuck script can never block the app; the engine stops itself at the timeout.
        return await Task.Run(() => Execute(code, lim, timeout, cap, ct), ct).WaitAsync(timeout + TimeSpan.FromSeconds(3), ct)
            .ContinueWith(t => t.IsCompletedSuccessfully ? t.Result
                : ct.IsCancellationRequested ? throw new OperationCanceledException(ct)
                : "Error: the code did not finish in time.", TaskScheduler.Default);
    }

    static string Execute(string code, CodeLimits lim, TimeSpan timeout, int cap, CancellationToken ct)
    {
        var output = new StringBuilder();
        var cut = false;
        void Print(string line)
        {
            if (output.Length >= cap) { cut = true; return; }
            output.AppendLine(line);
        }

        try
        {
            using var engine = new Engine(o =>
            {
                o.TimeoutInterval(timeout);
                o.LimitMemory((long)Math.Clamp(lim.MemoryMb, 2, 1024) * 1024 * 1024);
                o.LimitRecursion(Math.Clamp(lim.MaxRecursion, 20, 2000));
                if (lim.MaxStatements > 0) o.MaxStatements(lim.MaxStatements);
                o.CancellationToken(ct);
            });
            engine.SetValue("__print", new Action<string>(Print));
            engine.Execute(
                "var console={log:function(){__print(Array.prototype.slice.call(arguments).map(function(a){" +
                "if(typeof a==='object'&&a!==null){try{return JSON.stringify(a)}catch(e){return String(a)}}return String(a)}).join(' '))}};" +
                "console.info=console.log;console.warn=console.log;console.error=console.log;var print=console.log;");

            var value = engine.Evaluate(code);
            if (!value.IsUndefined())
            {
                engine.SetValue("__r", value);
                var text = engine.Evaluate("(typeof __r==='object'&&__r!==null)?JSON.stringify(__r):String(__r)").ToString();
                if (text.Length > 0) Print("=> " + text);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TimeoutException) { Print($"Error: the code ran longer than {timeout.TotalSeconds:0} seconds and was stopped."); }
        catch (Exception ex)
        {
            var name = ex.GetType().Name;
            Print(name switch
            {
                "MemoryLimitExceededException" => $"Error: the code used more than {lim.MemoryMb} MB of memory and was stopped.",
                "StatementsCountOverflowException" => "Error: the code ran too many steps and was stopped (endless loop?).",
                "RecursionDepthOverflowException" => "Error: the recursion is too deep.",
                _ => "Error: " + ex.Message
            });
        }

        var s = output.ToString().TrimEnd();
        if (s.Length > cap) { s = s[..cap]; cut = true; }
        if (cut) s += "\n[...output cut]";
        return s.Length == 0 ? "(no output; print results with console.log)" : s;
    }
}
