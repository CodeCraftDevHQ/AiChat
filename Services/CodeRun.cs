using System.ComponentModel;
using System.Diagnostics;
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

/// <summary>One language the model may run code in: the built-in JavaScript sandbox or a program on this computer.</summary>
public class CodeLanguage
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>true = the built-in JavaScript sandbox (Jint); false = an external program started with Command and Args.</summary>
    public bool Builtin { get; set; }
    public bool Enabled { get; set; }
    /// <summary>Ask the user before every run in this language.</summary>
    public bool Ask { get; set; } = true;
    /// <summary>Program to start, e.g. python. Windows only.</summary>
    public string Command { get; set; } = "";
    /// <summary>Arguments; {file} = the script file, {dir} = its folder. Put quotes around them yourself.</summary>
    public string Args { get; set; } = "";
    /// <summary>File extension of the script, e.g. .py</summary>
    public string Extension { get; set; } = ".txt";
    /// <summary>Folder the program runs in. Empty = a new empty temporary folder that is deleted afterwards.</summary>
    public string WorkDir { get; set; } = "";
    /// <summary>Start the program without the environment variables of the app (so keys and secrets are not visible to it).</summary>
    public bool CleanEnv { get; set; } = true;
    /// <summary>Time limit for this language in seconds (0 = the common limit).</summary>
    public int TimeoutSec { get; set; }

    public static List<CodeLanguage> Defaults() => new()
    {
        new() { Id = "javascript", Name = "JavaScript", Builtin = true, Enabled = true },
        new() { Id = "python", Name = "Python", Command = "python", Args = "-X utf8 \"{file}\"", Extension = ".py" },
        new() { Id = "powershell", Name = "PowerShell", Command = "powershell.exe", Args = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{file}\"", Extension = ".ps1" },
        new() { Id = "cmd", Name = "CMD (batch)", Command = "cmd.exe", Args = "/d /s /c \"\"{file}\"\"", Extension = ".bat" },
        new() { Id = "node", Name = "Node.js", Command = "node", Args = "\"{file}\"", Extension = ".js" },
        new() { Id = "csharp", Name = "C# script (dotnet-script)", Command = "dotnet-script", Args = "\"{file}\"", Extension = ".csx" },
    };
}

/// <summary>
/// Runs JavaScript with Jint, a JavaScript interpreter written in .NET. There is no browser, no Node.js,
/// no files, no network and no access to .NET types: the code can only compute and print.
/// </summary>
public static class CodeRunner
{
    /// <summary>Other programs can be started on Windows only (tests may switch this on).</summary>
    public static bool AllowAnyOs;
    public static bool ExternalAllowed => AllowAnyOs || OperatingSystem.IsWindows();

    static readonly string[] KeepEnv =
    {
        "PATH", "PATHEXT", "SystemRoot", "SYSTEMROOT", "windir", "SystemDrive", "ComSpec", "COMSPEC", "TEMP", "TMP", "TMPDIR",
        "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "HOME", "APPDATA", "LOCALAPPDATA", "ProgramFiles", "ProgramFiles(x86)",
        "ProgramData", "ProgramW6432", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "OS", "LANG"
    };

    /// <summary>Runs the code in the given language and returns text for the model (never throws except on cancel).</summary>
    public static Task<string> RunAsync(CodeLanguage lang, string code, CodeLimits lim, CancellationToken ct) =>
        lang.Builtin ? RunAsync(code, lim, ct) : RunExternalAsync(lang, code, lim, ct);

    public static async Task<string> RunExternalAsync(CodeLanguage lang, string code, CodeLimits lim, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code)) return "Error: empty code.";
        if (!ExternalAllowed) return "Error: running other programs is supported on Windows only. Use JavaScript.";
        if (string.IsNullOrWhiteSpace(lang.Command)) return $"Error: no program is set for {lang.Name}.";

        var cap = Math.Clamp(lim.OutputChars, 200, 50000);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(lang.TimeoutSec > 0 ? lang.TimeoutSec : lim.TimeoutSec, 1, 600));
        var dir = Path.Combine(Path.GetTempPath(), "aichat-code-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        try
        {
            var ext = lang.Extension.Trim();
            if (ext.Length > 0 && !ext.StartsWith('.')) ext = "." + ext;
            var file = Path.Combine(dir, "main" + (ext.Length > 0 ? ext : ".txt"));
            await File.WriteAllTextAsync(file, code, new UTF8Encoding(ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase)), ct);

            var work = lang.WorkDir.Trim().Length > 0 && Directory.Exists(lang.WorkDir.Trim()) ? lang.WorkDir.Trim() : dir;
            var psi = new ProcessStartInfo(lang.Command.Trim(), lang.Args.Replace("{file}", file).Replace("{dir}", dir))
            {
                WorkingDirectory = work,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            if (lang.CleanEnv)
            {
                var keep = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var k in KeepEnv)
                    if (Environment.GetEnvironmentVariable(k) is { } v) keep[k] = v;
                psi.Environment.Clear();
                foreach (var (k, v) in keep) psi.Environment[k] = v;
            }
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONUTF8"] = "1";

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var cut = false;
            void Add(StringBuilder sb, string? line)
            {
                if (line == null) return;
                lock (sb) { if (sb.Length >= cap) cut = true; else sb.AppendLine(line); }
            }

            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) => Add(stdout, e.Data);
            proc.ErrorDataReceived += (_, e) => Add(stderr, e.Data);
            try { proc.Start(); }
            catch (Win32Exception)
            {
                return $"Error: the program '{lang.Command.Trim()}' could not be started. It is probably not installed or not in PATH; the user can fix this in the settings (code execution).";
            }
            catch (Exception ex) { return "Error: could not start the program: " + ex.Message; }
            JobObject.Add(proc);
            try { proc.StandardInput.Close(); } catch { }
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var timedOut = false;
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                if (ct.IsCancellationRequested) throw;
                timedOut = true;
            }
            if (!timedOut) proc.WaitForExit();   // lets the last output lines arrive

            var sbOut = new StringBuilder();
            lock (stdout) sbOut.Append(stdout.ToString().TrimEnd());
            string err; lock (stderr) err = stderr.ToString().TrimEnd();
            if (err.Length > 0) sbOut.Append(sbOut.Length > 0 ? "\n" : "").Append("[stderr]\n").Append(err);
            if (timedOut) sbOut.Append(sbOut.Length > 0 ? "\n" : "").Append($"Error: the program ran longer than {timeout.TotalSeconds:0} seconds and was stopped.");
            else if (proc.ExitCode != 0) sbOut.Append(sbOut.Length > 0 ? "\n" : "").Append($"[exit code {proc.ExitCode}]");
            if (cut) sbOut.Append("\n[...output cut]");
            return sbOut.Length == 0 ? "(no output; print results to the standard output)" : sbOut.ToString();
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

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
