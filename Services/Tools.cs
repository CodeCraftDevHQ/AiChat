using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AiChat.Services;

/// <summary>
/// Tools ("function calling"): the model may ask the app to run a function (search the web, read a page,
/// calculate, get the date...) and receives the result, then continues its answer.
/// </summary>
public class ToolDef
{
    public string Name = "";
    /// <summary>English description for the model.</summary>
    public string Description = "";
    /// <summary>JSON schema of the arguments.</summary>
    public JsonObject Parameters = new() { ["type"] = "object", ["properties"] = new JsonObject() };
    /// <summary>Short name shown to the user next to a call.</summary>
    public string Label = "";
    public Func<JsonObject, CancellationToken, Task<string>> Run = (_, _) => Task.FromResult("");
    /// <summary>Always ask the user before this tool runs.</summary>
    public bool Ask;
    /// <summary>Decides from the arguments whether to ask (used when <see cref="Ask"/> is false).</summary>
    public Func<JsonObject, bool>? AskFor;
    /// <summary>"" = built in, "mcp" = from an MCP server.</summary>
    public string Source = "";
    /// <summary>Text shown in the permission question instead of the plain argument list.</summary>
    public Func<JsonObject, string>? Preview;
}

/// <summary>The tools offered to the model for one request, with the limits.</summary>
public class ToolSet
{
    public List<ToolDef> Tools = new();
    public int MaxRounds = 5;
    public int ResultMaxChars = 6000;
    public bool ShowCalls = true;
    /// <summary>Called before a tool runs; false = the user refused. Null = always allowed.</summary>
    public Func<ToolDef, JsonObject, Task<bool>>? Approve;
    /// <summary>Gets a line for the app log when a tool fails.</summary>
    public Action<string>? Log;

    public ToolDef? Find(string name) => Tools.FirstOrDefault(t => t.Name == name);

    /// <summary>OpenAI "tools" array.</summary>
    public JsonArray ToOpenAi()
    {
        var arr = new JsonArray();
        foreach (var t in Tools)
            arr.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = t.Parameters.DeepClone()
                }
            });
        return arr;
    }

    /// <summary>Claude "tools" array.</summary>
    public JsonArray ToAnthropic()
    {
        var arr = new JsonArray();
        foreach (var t in Tools)
            arr.Add(new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["input_schema"] = t.Parameters.DeepClone() });
        return arr;
    }

    /// <summary>Gemini "tools" array (one entry with all function declarations; no schema for tools without arguments).</summary>
    public JsonArray ToGemini()
    {
        var decls = new JsonArray();
        foreach (var t in Tools)
        {
            var d = new JsonObject { ["name"] = t.Name, ["description"] = t.Description };
            if (t.Parameters["properties"] is JsonObject { Count: > 0 }) d["parameters"] = McpSchema.ForGemini(t.Parameters);
            decls.Add(d);
        }
        return new JsonArray(new JsonObject { ["functionDeclarations"] = decls });
    }

    public static JsonObject ParseArgs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try { return JsonNode.Parse(json) as JsonObject ?? new JsonObject(); }
        catch { return new JsonObject(); }
    }

    /// <summary>Runs a tool; problems become text for the model instead of exceptions.</summary>
    public async Task<string> ExecuteAsync(string name, string? argsJson, CancellationToken ct)
    {
        var tool = Find(name);
        if (tool == null) return $"Error: unknown tool '{name}'.";
        var args = ParseArgs(argsJson);
        try
        {
            if (Approve != null && !await Approve(tool, args)) return "The user did not allow this tool call.";
            var text = await tool.Run(args, ct);
            text ??= "";
            if (text.StartsWith("Error", StringComparison.Ordinal)) Log?.Invoke($"Tool '{name}' returned: {text[..Math.Min(text.Length, 160)]}");
            var max = Math.Clamp(ResultMaxChars, 500, 50000);
            return text.Length > max ? text[..max] + "\n[...cut]" : text;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { Log?.Invoke($"Tool '{name}' timed out"); return "Error: the tool timed out. This is temporary; you may try once more."; }
        catch (Exception ex)
        {
            Log?.Invoke($"Tool '{name}' failed: {ex.Message}");
            var hint = name is "web_search" or "read_page"
                ? " This is a temporary problem with the network or the search service, not a missing tool. Try once more with a different query; if it fails again, tell the user the search service is unreachable right now and answer from what you know."
                : "";
            return "Error: " + ex.Message + hint;
        }
    }

    /// <summary>The line shown in the answer for a call: "> 🔧 Label: first argument".</summary>
    public string CallLine(string name, string? argsJson)
    {
        var tool = Find(name);
        var args = ParseArgs(argsJson);
        var first = args.Select(kv => kv.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : kv.Value?.ToJsonString())
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "";
        first = Regex.Replace(first, @"\s+", " ").Trim();
        if (first.Length > 90) first = first[..90] + "…";
        return $"> 🔧 {tool?.Label ?? name}{(first.Length > 0 ? ": " + first : "")}\n\n";
    }
}

public static class ToolMarks
{
    static readonly Regex Line = new(@"^> 🔧 .*(?:\r?\n)+", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Removes the "🔧 tool call" lines (they are for the user; the model must not see them again).</summary>
    public static string Strip(string text) =>
        text.Contains("> 🔧", StringComparison.Ordinal) ? Line.Replace(text, "").TrimStart() : text;
}

/// <summary>User settings of the tools (stored as JSON in the preferences).</summary>
public class ToolOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Use the tools of the MCP servers (when tools are on).</summary>
    public bool McpEnabled { get; set; } = true;
    public bool DateTime { get; set; } = true;
    public bool Calculator { get; set; } = true;
    public bool WebSearch { get; set; } = true;
    public bool ReadPage { get; set; } = true;
    /// <summary>The model may run code (JavaScript in a sandbox by default; it asks before every run).</summary>
    public bool CodeRun { get; set; } = true;
    /// <summary>Use the tools the user made (Settings > My tools).</summary>
    public bool CustomEnabled { get; set; } = true;
    /// <summary>The languages the model may use; each one has its own switch, question and settings.</summary>
    public List<CodeLanguage> CodeLanguages { get; set; } = CodeLanguage.Defaults();
    public CodeLimits Code { get; set; } = new();
    /// <summary>How many times the model may call tools in one answer.</summary>
    public int MaxRounds { get; set; } = 5;
    /// <summary>A tool result is cut to this many characters.</summary>
    public int ResultChars { get; set; } = 6000;
    /// <summary>Show a "🔧" line for every call inside the answer.</summary>
    public bool ShowCalls { get; set; } = true;
    /// <summary>Ask the user before a tool runs: 0 = never, 1 = only tools that send something to the internet (search, read page), 2 = every tool.</summary>
    public int AskMode { get; set; }

    // web search tool
    /// <summary>The model may choose, per search, how many results, how many pages to read and the time range (within the limits below).</summary>
    public bool SearchModelControls { get; set; } = true;
    /// <summary>Upper limit of results the model may ask for (1-10).</summary>
    public int SearchMaxResults { get; set; } = 10;
    /// <summary>Upper limit of pages the model may ask to be read per search (0 = it may not read pages).</summary>
    public int SearchMaxPages { get; set; } = 5;
    /// <summary>Searches allowed in one answer (0 = no limit besides the rounds above).</summary>
    public int MaxSearchCalls { get; set; }
    /// <summary>Extra instruction added to the system prompt when tools are on (empty = none).</summary>
    public string ExtraPrompt { get; set; } = "";
}

/// <summary>What the memory tools of the model do (the app supplies the code).</summary>
public class MemoryHooks
{
    public Func<string, string> Remember = _ => "";
    public Func<string, string> Forget = _ => "";
    public bool Ask = true;
}

public static class BuiltInTools
{
    static JsonObject Schema(params (string Name, string Type, string Desc, bool Required)[] props)
    {
        var p = new JsonObject();
        var req = new JsonArray();
        foreach (var (n, t, d, r) in props)
        {
            p[n] = new JsonObject { ["type"] = t, ["description"] = d };
            if (r) req.Add(n);
        }
        return new JsonObject { ["type"] = "object", ["properties"] = p, ["required"] = req };
    }

    static string Str(JsonObject a, string key) =>
        a[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : a[key]?.ToString() ?? "";

    public static ToolSet Build(ToolOptions o, WebSearchService search, string engine, string key, string baseUrl,
                                SearchOptions sopt, Func<DateTime>? now = null, MemoryHooks? memory = null)
    {
        now ??= () => DateTime.Now;
        var set = new ToolSet
        {
            MaxRounds = Math.Clamp(o.MaxRounds, 1, 20),
            ResultMaxChars = Math.Clamp(o.ResultChars, 500, 50000),
            ShowCalls = o.ShowCalls
        };

        if (memory != null)
        {
            set.Tools.Add(new ToolDef
            {
                Name = "remember",
                Label = "Memory: save",
                Description = "Saves ONE short, durable fact about the user to long-term memory (a name, job, preference, language, ongoing project). " +
                              "Call it ONLY when the user explicitly asks you to remember something (e.g. \"remember that ...\"). " +
                              "Never call it to answer a question or on your own; never save secrets, passwords, ID or card numbers, or temporary details. " +
                              "After it returns, continue and answer the user normally.",
                Parameters = Schema(("text", "string", "The fact, as one short sentence in the user's language", true)),
                Ask = memory.Ask,
                Run = (a, _) => Task.FromResult(memory.Remember(Str(a, "text")))
            });
            set.Tools.Add(new ToolDef
            {
                Name = "forget",
                Label = "Memory: delete",
                Description = "Deletes a saved memory. Call it ONLY when the user explicitly asks you to forget something. Pass a distinctive part of the memory text. After it returns, continue and answer the user normally.",
                Parameters = Schema(("text", "string", "A distinctive part of the memory to delete", true)),
                Ask = memory.Ask,
                Run = (a, _) => Task.FromResult(memory.Forget(Str(a, "text")))
            });
        }

        if (o.DateTime)
            set.Tools.Add(new ToolDef
            {
                Name = "get_datetime",
                Label = "Date & time",
                Description = "Returns the current local date, time, weekday and time zone of the user.",
                Run = (_, _) =>
                {
                    var n = now();
                    var tz = TimeZoneInfo.Local;
                    return Task.FromResult($"{n.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} ({n.DayOfWeek}), time zone: {tz.DisplayName}");
                }
            });

        if (o.Calculator)
            set.Tools.Add(new ToolDef
            {
                Name = "calculator",
                Label = "Calculator",
                Description = "Evaluates a math expression exactly. Supports + - * / % ^, parentheses, pi, e and the functions " +
                              "sqrt, abs, sin, cos, tan, asin, acos, atan, ln, log (base 10), exp, floor, ceil, round, min, max, pow.",
                Parameters = Schema(("expression", "string", "The expression, e.g. (12.5*3)^2 / sqrt(16)", true)),
                Run = (a, _) =>
                {
                    var r = MathEval.Eval(Str(a, "expression"));
                    return Task.FromResult(MathEval.Format(r));
                }
            });

        if (o.WebSearch)
        {
            var maxResults = Math.Clamp(o.SearchMaxResults, 1, 10);
            var maxPages = Math.Clamp(o.SearchMaxPages, 0, 10);
            var searches = 0;
            var props = new List<(string, string, string, bool)> { ("query", "string", "The search query", true) };
            if (o.SearchModelControls)
            {
                props.Add(("max_results", "integer", $"How many results you need (1-{maxResults}). Use few for simple facts, more for broad research.", false));
                if (maxPages > 0)
                    props.Add(("read_pages", "integer", $"How many of the top results to open and read in full (0-{maxPages}). Use it when snippets are not enough.", false));
                props.Add(("time_range", "string", "Only results from this period: day, week, month or year. Leave empty for any time.", false));
            }
            set.Tools.Add(new ToolDef
            {
                Name = "web_search",
                Label = "Web search",
                Description = "Searches the web and returns the top results (title, address, snippet). " +
                              "Use it for current events or facts you are unsure about. You may search several times with different queries.",
                Parameters = Schema(props.ToArray()),
                Run = async (a, ct) =>
                {
                    var q = Str(a, "query").Trim();
                    if (q.Length == 0) return "Error: empty query.";
                    if (o.MaxSearchCalls > 0 && ++searches > o.MaxSearchCalls)
                        return $"Search limit reached ({o.MaxSearchCalls} per answer). Answer with what you already have.";

                    var so = sopt;
                    if (o.SearchModelControls)
                    {
                        so = System.Text.Json.JsonSerializer.Deserialize<SearchOptions>(System.Text.Json.JsonSerializer.Serialize(sopt)) ?? sopt;
                        if (int.TryParse(Str(a, "max_results"), out var n)) so.Count = Math.Clamp(n, 1, maxResults);
                        else so.Count = Math.Min(so.Count, maxResults);
                        if (int.TryParse(Str(a, "read_pages"), out var rp)) so.PageCount = Math.Clamp(rp, 0, maxPages);
                        else so.PageCount = Math.Min(so.PageCount, maxPages);
                        var tr = Str(a, "time_range").Trim().ToLowerInvariant();
                        if (tr is "day" or "week" or "month" or "year") so.TimeRange = tr;
                    }

                    var found = await search.SearchAsync(engine, q, key, baseUrl, so, ct);
                    if (found.Count == 0) return "No results found for this query. The search worked, there is just nothing for these words: try shorter or different keywords, or another language.";
                    if (so.PageCount > 0) found = await search.ReadPagesAsync(found, so, ct);
                    var sb = new StringBuilder();
                    for (var i = 0; i < found.Count; i++)
                    {
                        sb.Append('[').Append(i + 1).Append("] ").AppendLine(found[i].Title);
                        sb.AppendLine(found[i].Url);
                        sb.AppendLine(found[i].Content.Length > 0 ? found[i].Content : found[i].Snippet);
                        sb.AppendLine();
                    }
                    return sb.ToString().Trim();
                }
            });
        }

        if (o.CodeRun)
        {
            var lim = o.Code ?? new CodeLimits();
            var langs = (o.CodeLanguages ?? new()).Where(l => l.Enabled && (l.Builtin || CodeRunner.ExternalAllowed)).ToList();
            if (langs.Count > 0)
            {
                CodeLanguage? Pick(JsonObject a)
                {
                    var id = Str(a, "language").Trim();
                    if (id.Length == 0) return langs[0];
                    return langs.FirstOrDefault(l => l.Id.Equals(id, StringComparison.OrdinalIgnoreCase) || l.Name.Equals(id, StringComparison.OrdinalIgnoreCase));
                }
                set.Tools.Add(new ToolDef
                {
                    Name = "run_code",
                    Label = "Run code",
                    AskFor = a => Pick(a)?.Ask ?? true,
                    Description = "Runs code and returns what it prints (standard output; in JavaScript also the value of the last expression). " +
                                  "Use it for exact calculations, data processing, parsing, sorting, dates, simulations and checking your own results. " +
                                  "Available languages (use the id): " + string.Join(", ", langs.Select(l => $"{l.Id} ({l.Name})")) + ". " +
                                  (langs.Any(l => l.Builtin) ? "JavaScript runs in a sandbox with no files, no network and no Node.js. " : "") +
                                  "Always print the results. The run is stopped after a time limit.",
                    Parameters = Schema(("code", "string", "The complete program. Print results to the standard output (console.log in JavaScript).", true),
                                        ("language", "string", "Language id. Default: " + langs[0].Id, false)),
                    Run = (a, ct) =>
                    {
                        var l = Pick(a);
                        if (l == null) return Task.FromResult("Error: unknown language. Available: " + string.Join(", ", langs.Select(x => x.Id)));
                        return CodeRunner.RunAsync(l, Str(a, "code"), lim, ct);
                    }
                });
            }
        }

        if (o.ReadPage)
            set.Tools.Add(new ToolDef
            {
                Name = "read_page",
                Label = "Read page",
                Description = "Opens a web page (http/https) and returns its readable text.",
                Parameters = Schema(("url", "string", "Full address of the page", true)),
                Run = async (a, ct) =>
                {
                    var url = Str(a, "url").Trim();
                    var blocked = sopt.BlockedDomains.Split(new[] { '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(d => d.ToLowerInvariant().TrimStart('*', '.')).ToList();
                    if (!WebSearchService.IsAllowed(url, blocked)) return "Error: this address is not allowed.";
                    var one = new SearchOptions
                    {
                        PageCount = 1,
                        PageChars = Math.Clamp(o.ResultChars, 300, 20000),
                        PageTimeoutSec = sopt.PageTimeoutSec,
                        BlockedDomains = sopt.BlockedDomains
                    };
                    var r = (await search.ReadPagesAsync(new List<SearchResult> { new("", url, "") }, one, ct))[0];
                    return r.Content.Length > 0 ? r.Content : "Error: the page could not be read (blocked, not a web page, or too slow). Try another result.";
                }
            });

        return set;
    }
}

/// <summary>Small safe math evaluator for the calculator tool (no code execution).</summary>
public static class MathEval
{
    public static string Format(double d) =>
        double.IsNaN(d) || double.IsInfinity(d) ? "Error: the result is not a finite number."
        : d.ToString("G15", CultureInfo.InvariantCulture);

    public static double Eval(string expr)
    {
        var p = new Parser(expr.Replace('×', '*').Replace('÷', '/').Replace('−', '-'));
        var v = p.ParseExpr();
        p.SkipWs();
        if (!p.End) throw new FormatException("Unexpected character at position " + (p.Pos + 1));
        return v;
    }

    sealed class Parser
    {
        readonly string _s;
        public int Pos;
        public Parser(string s) => _s = s;
        public bool End => Pos >= _s.Length;
        public void SkipWs() { while (Pos < _s.Length && char.IsWhiteSpace(_s[Pos])) Pos++; }
        bool Eat(char c) { SkipWs(); if (Pos < _s.Length && _s[Pos] == c) { Pos++; return true; } return false; }

        public double ParseExpr()
        {
            var v = ParseTerm();
            while (true)
            {
                if (Eat('+')) v += ParseTerm();
                else if (Eat('-')) v -= ParseTerm();
                else return v;
            }
        }

        double ParseTerm()
        {
            var v = ParseUnary();
            while (true)
            {
                if (Eat('*')) v *= ParseUnary();
                else if (Eat('/')) v /= ParseUnary();
                else if (Eat('%')) v %= ParseUnary();
                else return v;
            }
        }

        double ParseUnary()
        {
            if (Eat('-')) return -ParseUnary();
            if (Eat('+')) return ParseUnary();
            return ParsePower();
        }

        double ParsePower()
        {
            var b = ParsePrimary();
            if (Eat('^')) return Math.Pow(b, ParseUnary());   // right associative
            return b;
        }

        double ParsePrimary()
        {
            SkipWs();
            if (End) throw new FormatException("Unexpected end of the expression");
            if (Eat('(')) { var v = ParseExpr(); if (!Eat(')')) throw new FormatException("Missing )"); return v; }

            var c = _s[Pos];
            if (char.IsDigit(c) || c == '.')
            {
                var st = Pos;
                while (Pos < _s.Length && (char.IsDigit(_s[Pos]) || _s[Pos] == '.')) Pos++;
                if (Pos < _s.Length && (_s[Pos] == 'e' || _s[Pos] == 'E') && Pos + 1 < _s.Length &&
                    (char.IsDigit(_s[Pos + 1]) || ((_s[Pos + 1] == '-' || _s[Pos + 1] == '+') && Pos + 2 < _s.Length && char.IsDigit(_s[Pos + 2]))))
                {
                    Pos += 2;
                    while (Pos < _s.Length && char.IsDigit(_s[Pos])) Pos++;
                }
                if (!double.TryParse(_s.AsSpan(st, Pos - st), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                    throw new FormatException("Bad number");
                return n;
            }
            if (char.IsLetter(c))
            {
                var st = Pos;
                while (Pos < _s.Length && char.IsLetterOrDigit(_s[Pos])) Pos++;
                var name = _s[st..Pos].ToLowerInvariant();
                if (!Eat('('))
                    return name switch { "pi" => Math.PI, "e" => Math.E, _ => throw new FormatException("Unknown name: " + name) };
                var args = new List<double>();
                if (!Eat(')'))
                {
                    do args.Add(ParseExpr()); while (Eat(','));
                    if (!Eat(')')) throw new FormatException("Missing )");
                }
                return Call(name, args);
            }
            throw new FormatException("Unexpected character '" + c + "'");
        }

        static double Call(string f, List<double> a)
        {
            double A(int i) => i < a.Count ? a[i] : throw new FormatException($"{f}() needs more arguments");
            return f switch
            {
                "sqrt" => Math.Sqrt(A(0)),
                "abs" => Math.Abs(A(0)),
                "sin" => Math.Sin(A(0)),
                "cos" => Math.Cos(A(0)),
                "tan" => Math.Tan(A(0)),
                "asin" => Math.Asin(A(0)),
                "acos" => Math.Acos(A(0)),
                "atan" => Math.Atan(A(0)),
                "ln" => Math.Log(A(0)),
                "log" => Math.Log10(A(0)),
                "exp" => Math.Exp(A(0)),
                "floor" => Math.Floor(A(0)),
                "ceil" => Math.Ceiling(A(0)),
                "round" => a.Count > 1 ? Math.Round(A(0), (int)A(1), MidpointRounding.AwayFromZero) : Math.Round(A(0), MidpointRounding.AwayFromZero),
                "min" => a.Count > 0 ? a.Min() : throw new FormatException("min() needs arguments"),
                "max" => a.Count > 0 ? a.Max() : throw new FormatException("max() needs arguments"),
                "pow" => Math.Pow(A(0), A(1)),
                _ => throw new FormatException("Unknown function: " + f)
            };
        }
    }
}
