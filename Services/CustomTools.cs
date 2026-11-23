using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AiChat.Services;

/// <summary>One argument of a custom tool.</summary>
public class CustomToolParam
{
    public string Name { get; set; } = "";
    /// <summary>string | number | integer | boolean</summary>
    public string Type { get; set; } = "string";
    public string Description { get; set; } = "";
    public bool Required { get; set; } = true;
}

/// <summary>A tool made by the user. Type: "js" | "http" | "command" | "prompt".</summary>
public class CustomTool
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    /// <summary>Name the model sees (letters, digits, _ and -).</summary>
    public string Name { get; set; } = "";
    /// <summary>Short name shown to the user next to a call.</summary>
    public string Label { get; set; } = "";
    /// <summary>What the tool does and when to use it (for the model, best in English).</summary>
    public string Description { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Type { get; set; } = "js";
    /// <summary>Ask the user before every run.</summary>
    public bool Ask { get; set; } = true;
    public List<CustomToolParam> Params { get; set; } = new();
    public int TimeoutSec { get; set; } = 20;
    /// <summary>The result is cut to this many characters (0 = the common limit of the tools).</summary>
    public int MaxChars { get; set; }

    // js: the code sees the arguments as the object "args"; print results with console.log or end with an expression
    public string Code { get; set; } = "";

    // http: {{name}} is replaced by the argument (URL-encoded inside the URL); {{json:name}} = a JSON string, {{raw:name}} = as is
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = "";
    /// <summary>One "Name: value" per line.</summary>
    public string Headers { get; set; } = "";
    public string Body { get; set; } = "";
    /// <summary>Part of a JSON answer to return, like data.items[0].name (empty = the whole answer).</summary>
    public string ResponsePath { get; set; } = "";

    // command (Windows): the program and one argument per line ({{name}} allowed); no shell is used
    public string Command { get; set; } = "";
    public string Args { get; set; } = "";
    public string WorkDir { get; set; } = "";
    public bool CleanEnv { get; set; } = true;

    // prompt: instructions returned to the model with {{name}} filled in
    public string Template { get; set; } = "";

    public CustomTool Clone() => JsonSerializer.Deserialize<CustomTool>(JsonSerializer.Serialize(this))!;
}

public static class ToolTemplate
{
    static readonly Regex Slot = new(@"\{\{\s*(?:(json|url|raw)\s*:\s*)?([A-Za-z_][A-Za-z0-9_]*)\s*\}\}", RegexOptions.Compiled);

    public static string ArgText(JsonObject args, string name)
    {
        if (!args.TryGetPropertyValue(name, out var n) || n == null) return "";
        return n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n.ToJsonString();
    }

    /// <summary>Replaces {{name}} slots. defaultMode: "raw" or "url" (URL-encoded).</summary>
    public static string Fill(string template, JsonObject args, string defaultMode = "raw") =>
        Slot.Replace(template ?? "", m =>
        {
            var mode = m.Groups[1].Success ? m.Groups[1].Value : defaultMode;
            var text = ArgText(args, m.Groups[2].Value);
            return mode switch
            {
                "url" => Uri.EscapeDataString(text),
                "json" => JsonSerializer.Serialize(text, JsonRelaxed),
                _ => text
            };
        });

    static readonly JsonSerializerOptions JsonRelaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Names used in {{...}} slots.</summary>
    public static List<string> Slots(string template) =>
        Slot.Matches(template ?? "").Select(m => m.Groups[2].Value).Distinct().ToList();
}

/// <summary>Picks a part of a JSON document by a simple path: a.b[0].c</summary>
public static class JsonPathLite
{
    public static bool TryGet(JsonNode? root, string path, out JsonNode? found)
    {
        found = root;
        foreach (var raw in Regex.Split(path.Trim(), @"\.(?![^\[]*\])").Where(x => x.Length > 0))
        {
            var m = Regex.Match(raw, @"^([^\[\]]*)((?:\[\d+\])*)$");
            if (!m.Success) return false;
            if (m.Groups[1].Value.Length > 0)
            {
                if (found is not JsonObject o || !o.TryGetPropertyValue(m.Groups[1].Value, out found)) return false;
            }
            foreach (Match idx in Regex.Matches(m.Groups[2].Value, @"\[(\d+)\]"))
            {
                var i = int.Parse(idx.Groups[1].Value);
                if (found is not JsonArray a || i >= a.Count) return false;
                found = a[i];
            }
        }
        return true;
    }
}

public static class CustomToolCheck
{
    static readonly Regex NameOk = new(@"^[A-Za-z][A-Za-z0-9_-]{0,63}$", RegexOptions.Compiled);
    static readonly Regex ParamOk = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    public static string Sanitize(string name)
    {
        var s = Regex.Replace(name ?? "", @"[^A-Za-z0-9_-]+", "_").Trim('_', '-');
        if (s.Length == 0 || !char.IsLetter(s[0])) s = "tool_" + s;
        return s.Length > 64 ? s[..64] : s;
    }

    /// <summary>Problems that stop the tool from working (empty list = fine).</summary>
    public static List<string> Validate(CustomTool t)
    {
        var p = new List<string>();
        if (!NameOk.IsMatch(t.Name ?? "")) p.Add("name");
        if (string.IsNullOrWhiteSpace(t.Description)) p.Add("description");
        if (t.Params.Any(x => !ParamOk.IsMatch(x.Name ?? ""))) p.Add("param-name");
        if (t.Params.GroupBy(x => x.Name).Any(g => g.Count() > 1)) p.Add("param-duplicate");
        switch (t.Type)
        {
            case "js": if (string.IsNullOrWhiteSpace(t.Code)) p.Add("code"); break;
            case "http":
                if (!Uri.TryCreate(Regex.Replace(t.Url ?? "", @"\{\{[^}]*\}\}", "x"), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) p.Add("url");
                break;
            case "command": if (string.IsNullOrWhiteSpace(t.Command)) p.Add("command"); break;
            case "prompt": if (string.IsNullOrWhiteSpace(t.Template)) p.Add("template"); break;
            default: p.Add("type"); break;
        }
        return p;
    }
}

public static class CustomToolRunner
{
    public static async Task<string> RunAsync(CustomTool t, JsonObject args, CodeLimits lim, HttpClient http, CancellationToken ct)
    {
        var timeout = Math.Clamp(t.TimeoutSec <= 0 ? lim.TimeoutSec : t.TimeoutSec, 1, 600);
        var cap = t.MaxChars > 0 ? t.MaxChars : lim.OutputChars;
        var res = t.Type switch
        {
            "js" => await RunJsAsync(t, args, lim, timeout, ct),
            "http" => await RunHttpAsync(t, args, http, timeout, cap, ct),
            "command" => await RunCommandAsync(t, args, timeout, cap, ct),
            "prompt" => ToolTemplate.Fill(t.Template, args),
            _ => "Error: unknown tool type."
        };
        return res.Length > cap + 50 ? res[..cap] + "\n[...cut]" : res;
    }

    static Task<string> RunJsAsync(CustomTool t, JsonObject args, CodeLimits lim, int timeout, CancellationToken ct)
    {
        var l = new CodeLimits { TimeoutSec = timeout, MemoryMb = lim.MemoryMb, OutputChars = t.MaxChars > 0 ? t.MaxChars : lim.OutputChars, MaxStatements = lim.MaxStatements, MaxRecursion = lim.MaxRecursion };
        return CodeRunner.RunAsync("var args = " + args.ToJsonString() + ";\n" + t.Code, l, ct);
    }

    static async Task<string> RunHttpAsync(CustomTool t, JsonObject args, HttpClient http, int timeout, int cap, CancellationToken ct)
    {
        var url = ToolTemplate.Fill(t.Url, args, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return "Error: the address of the tool is not a valid http(s) URL: " + url;
        var method = new HttpMethod((string.IsNullOrWhiteSpace(t.Method) ? "GET" : t.Method).Trim().ToUpperInvariant());
        using var req = new HttpRequestMessage(method, uri);
        var hasType = false;
        foreach (var line in (t.Headers ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = line.IndexOf(':');
            if (i <= 0) continue;
            var name = line[..i].Trim();
            var val = ToolTemplate.Fill(line[(i + 1)..].Trim(), args);
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) { hasType = true; continue; }
            req.Headers.TryAddWithoutValidation(name, val);
        }
        if (!string.IsNullOrWhiteSpace(t.Body) && method != HttpMethod.Get && method != HttpMethod.Head)
        {
            var body = ToolTemplate.Fill(t.Body, args);
            var type = (t.Headers ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Content-Type", StringComparison.OrdinalIgnoreCase));
            var ctype = hasType && type != null ? type[(type.IndexOf(':') + 1)..].Trim() : body.TrimStart().StartsWith('{') || body.TrimStart().StartsWith('[') ? "application/json" : "text/plain";
            req.Content = new StringContent(body, Encoding.UTF8, ctype.Split(';')[0].Trim());
        }
        if (!req.Headers.UserAgent.Any()) req.Headers.UserAgent.ParseAdd("AiChat-tool/1.0");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));
        try
        {
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var text = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode)
                return $"Error: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}\n" + (text.Length > 500 ? text[..500] : text);
            if (!string.IsNullOrWhiteSpace(t.ResponsePath))
            {
                try
                {
                    if (JsonPathLite.TryGet(JsonNode.Parse(text), t.ResponsePath, out var part))
                        return part is JsonValue v && v.TryGetValue<string>(out var s) ? s : part?.ToJsonString() ?? "null";
                    return $"Error: '{t.ResponsePath}' was not found in the answer. The answer was:\n" + (text.Length > cap ? text[..cap] : text);
                }
                catch (JsonException) { /* not JSON: return the text */ }
            }
            return text.Trim().Length == 0 ? "(empty answer)" : text;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return $"Error: the request took longer than {timeout} seconds."; }
        catch (HttpRequestException ex) { return "Error: the request failed: " + ex.Message; }
    }

    /// <summary>The program and its arguments (one per line, {{name}} filled in) for the command type.</summary>
    public static (string Program, List<string> Args) CommandLine(CustomTool t, JsonObject args) =>
        (t.Command.Trim(), (t.Args ?? "").Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0)
            .Select(l => ToolTemplate.Fill(l, args)).ToList());

    static async Task<string> RunCommandAsync(CustomTool t, JsonObject args, int timeout, int cap, CancellationToken ct)
    {
        if (!CodeRunner.ExternalAllowed) return "Error: running programs is supported on Windows only.";
        var (program, list) = CommandLine(t, args);
        if (program.Length == 0) return "Error: no program is set for this tool.";

        var psi = new ProcessStartInfo(program)
        {
            WorkingDirectory = t.WorkDir.Trim().Length > 0 && Directory.Exists(t.WorkDir.Trim()) ? t.WorkDir.Trim() : Path.GetTempPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var a in list) psi.ArgumentList.Add(a);
        if (t.CleanEnv)
        {
            var keep = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in new[] { "PATH", "PATHEXT", "SystemRoot", "windir", "SystemDrive", "ComSpec", "TEMP", "TMP", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "ProgramFiles", "ProgramFiles(x86)", "ProgramData", "OS" })
                if (Environment.GetEnvironmentVariable(k) is { } v) keep[k] = v;
            psi.Environment.Clear();
            foreach (var (k, v) in keep) psi.Environment[k] = v;
        }

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
        catch (System.ComponentModel.Win32Exception) { return $"Error: the program '{program}' could not be started (not installed, wrong path, or not in PATH)."; }
        catch (Exception ex) { return "Error: could not start the program: " + ex.Message; }
        JobObject.Add(proc);
        try { proc.StandardInput.Close(); } catch { }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));
        var timedOut = false;
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            if (ct.IsCancellationRequested) throw;
            timedOut = true;
        }
        if (!timedOut) proc.WaitForExit();

        var sb = new StringBuilder();
        lock (stdout) sb.Append(stdout.ToString().TrimEnd());
        string err; lock (stderr) err = stderr.ToString().TrimEnd();
        if (err.Length > 0) sb.Append(sb.Length > 0 ? "\n" : "").Append("[stderr]\n").Append(err);
        if (timedOut) sb.Append(sb.Length > 0 ? "\n" : "").Append($"Error: the program ran longer than {timeout} seconds and was stopped.");
        else if (proc.ExitCode != 0) sb.Append(sb.Length > 0 ? "\n" : "").Append($"[exit code {proc.ExitCode}]");
        if (cut) sb.Append("\n[...output cut]");
        return sb.Length == 0 ? "(no output)" : sb.ToString();
    }
}

public static class CustomToolBuilder
{
    public static JsonObject Schema(CustomTool t)
    {
        var props = new JsonObject();
        var req = new JsonArray();
        foreach (var p in t.Params)
        {
            props[p.Name] = new JsonObject
            {
                ["type"] = p.Type is "number" or "integer" or "boolean" ? p.Type : "string",
                ["description"] = p.Description ?? ""
            };
            if (p.Required) req.Add(p.Name);
        }
        return new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = req };
    }

    /// <summary>What the user is shown when a tool asks for permission.</summary>
    public static string Preview(CustomTool t, JsonObject args)
    {
        var a = string.Join("\n", args.Select(kv => kv.Key + ": " + ToolTemplate.ArgText(args, kv.Key)));
        return t.Type switch
        {
            "http" => $"{(string.IsNullOrWhiteSpace(t.Method) ? "GET" : t.Method.ToUpperInvariant())} {ToolTemplate.Fill(t.Url, args, "url")}\n\n{a}".Trim(),
            "command" => CommandText(t, args) + (a.Length > 0 ? "\n\n" + a : ""),
            _ => a
        };
    }

    static string CommandText(CustomTool t, JsonObject args)
    {
        var (prog, list) = CustomToolRunner.CommandLine(t, args);
        return prog + " " + string.Join(" ", list.Select(x => x.Contains(' ') ? "\"" + x + "\"" : x));
    }

    /// <summary>The tools for the model. Names that are already taken (built-in / MCP) get a suffix.</summary>
    public static List<ToolDef> BuildAll(IEnumerable<CustomTool> tools, CodeLimits lim, HttpClient http, ISet<string>? taken = null)
    {
        var used = new HashSet<string>(taken ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
        var res = new List<ToolDef>();
        foreach (var t in tools.Where(x => x.Enabled))
        {
            if (CustomToolCheck.Validate(t).Count > 0) continue;
            if (t.Type == "command" && !CodeRunner.ExternalAllowed) continue;
            var name = CustomToolCheck.Sanitize(t.Name);
            var n = name;
            for (var i = 2; !used.Add(n); i++) n = name + "_" + i;
            var tool = t;
            res.Add(new ToolDef
            {
                Name = n,
                Label = string.IsNullOrWhiteSpace(t.Label) ? t.Name : t.Label,
                Description = t.Description,
                Parameters = Schema(t),
                Ask = t.Ask,
                Source = "custom",
                Preview = a => Preview(tool, a),
                Run = (a, ct) => CustomToolRunner.RunAsync(tool, a, lim, http, ct)
            });
        }
        return res;
    }
}

public static class CustomToolIo
{
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Export(IEnumerable<CustomTool> tools) =>
        JsonSerializer.Serialize(new { tools = tools.Select(x => { var c = x.Clone(); return c; }).ToList() }, Pretty);

    /// <summary>Reads tools from JSON: {"tools":[...]}, an array, or one tool object. New ids are given.</summary>
    public static (List<CustomTool> Tools, string? Error) Import(string json)
    {
        try
        {
            var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            JsonArray arr = node switch
            {
                JsonArray a => a,
                JsonObject o when o["tools"] is JsonArray ta => ta,
                JsonObject o => new JsonArray(o.DeepClone()),
                _ => throw new JsonException("not a tool")
            };
            var list = new List<CustomTool>();
            foreach (var item in arr)
            {
                var t = item.Deserialize<CustomTool>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (t == null) continue;
                t.Id = Guid.NewGuid().ToString("N")[..10];
                t.Name = CustomToolCheck.Sanitize(t.Name);
                if (!new[] { "js", "http", "command", "prompt" }.Contains(t.Type)) t.Type = "js";
                list.Add(t);
            }
            return list.Count == 0 ? (list, "empty") : (list, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            return (new(), ex.Message);
        }
    }

    public static List<CustomTool> Examples() => new()
    {
        new()
        {
            Name = "word_count", Label = "Word count", Type = "js", Ask = false,
            Description = "Counts the words and characters of a text. Use it when the user asks how long a text is.",
            Params = { new() { Name = "text", Description = "The text to count" } },
            Code = "var t = String(args.text || '');\nvar words = t.trim() ? t.trim().split(/\\s+/).length : 0;\nconsole.log(JSON.stringify({ words: words, characters: t.length }));"
        },
        new()
        {
            Name = "get_weather", Label = "Weather", Type = "http", Ask = true, Method = "GET",
            Description = "Gets the current weather of a city (temperature, condition, wind). Use it when the user asks about the weather.",
            Params = { new() { Name = "city", Description = "City name in English, e.g. Tehran" } },
            Url = "https://wttr.in/{{city}}?format=3"
        },
        new()
        {
            Name = "meeting_minutes", Label = "Meeting minutes", Type = "prompt", Ask = false,
            Description = "Use when the user gives raw meeting notes and wants minutes. Returns the format to follow.",
            Params = { new() { Name = "notes", Description = "The raw meeting notes" } },
            Template = "Write structured meeting minutes from the notes below with these sections: Summary (3 lines), Decisions, Action items (owner and deadline if mentioned), Open questions. Use the language of the notes.\n\nNotes:\n{{notes}}"
        },
        new()
        {
            Name = "ping_host", Label = "Ping", Type = "command", Ask = true, Command = "ping", Args = "-n\n2\n{{host}}", TimeoutSec = 15,
            Description = "Pings a host name or IP address twice and returns the output (Windows only).",
            Params = { new() { Name = "host", Description = "Host name or IP address" } }
        }
    };
}
