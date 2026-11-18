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
            var max = Math.Clamp(ResultMaxChars, 500, 50000);
            return text.Length > max ? text[..max] + "\n[...cut]" : text;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return "Error: the tool timed out."; }
        catch (Exception ex) { return "Error: " + ex.Message; }
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
    public bool Enabled { get; set; }
    public bool DateTime { get; set; } = true;
    public bool Calculator { get; set; } = true;
    public bool WebSearch { get; set; } = true;
    public bool ReadPage { get; set; } = true;
    /// <summary>How many times the model may call tools in one answer.</summary>
    public int MaxRounds { get; set; } = 5;
    /// <summary>A tool result is cut to this many characters.</summary>
    public int ResultChars { get; set; } = 6000;
    /// <summary>Show a "🔧" line for every call inside the answer.</summary>
    public bool ShowCalls { get; set; } = true;
    /// <summary>Extra instruction added to the system prompt when tools are on (empty = none).</summary>
    public string ExtraPrompt { get; set; } = "";
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
                                SearchOptions sopt, Func<DateTime>? now = null)
    {
        now ??= () => DateTime.Now;
        var set = new ToolSet
        {
            MaxRounds = Math.Clamp(o.MaxRounds, 1, 20),
            ResultMaxChars = Math.Clamp(o.ResultChars, 500, 50000),
            ShowCalls = o.ShowCalls
        };

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
            set.Tools.Add(new ToolDef
            {
                Name = "web_search",
                Label = "Web search",
                Description = "Searches the web and returns the top results (title, address, snippet). " +
                              "Use it for current events or facts you are unsure about.",
                Parameters = Schema(("query", "string", "The search query", true)),
                Run = async (a, ct) =>
                {
                    var q = Str(a, "query").Trim();
                    if (q.Length == 0) return "Error: empty query.";
                    var found = await search.SearchAsync(engine, q, key, baseUrl, sopt, ct);
                    if (found.Count == 0) return "No results.";
                    if (sopt.PageCount > 0) found = await search.ReadPagesAsync(found, sopt, ct);
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
                    return r.Content.Length > 0 ? r.Content : "Error: the page could not be read.";
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
