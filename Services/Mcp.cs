using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AiChat.Services;

/// <summary>A remote MCP server (Streamable HTTP). The headers (often a token) are kept in secure storage, not in this JSON.</summary>
public class McpServerConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>"http" (remote server) or "stdio" (a program on this PC; Windows only).</summary>
    public string Transport { get; set; } = "http";
    /// <summary>stdio: the program (e.g. npx, uvx, node, python).</summary>
    public string Command { get; set; } = "";
    /// <summary>stdio: one argument per entry.</summary>
    public List<string> Args { get; set; } = new();
    /// <summary>stdio: working folder (empty = default).</summary>
    public string WorkDir { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Ask the user before every tool of this server runs.</summary>
    public bool Ask { get; set; }
    public int TimeoutSec { get; set; } = 30;
    /// <summary>Tools (original names) the user switched off.</summary>
    public List<string> DisabledTools { get; set; } = new();
    /// <summary>One "Name: value" per line (e.g. Authorization: Bearer ...). Not serialized with the list.</summary>
    [JsonIgnore] public string Headers { get; set; } = "";
    /// <summary>stdio: one KEY=value per line (API keys for the program). Kept in secure storage.</summary>
    [JsonIgnore] public string Env { get; set; } = "";

    [JsonIgnore] public bool IsStdio => Transport == "stdio";

    public McpServerConfig Copy() => new()
    {
        Id = Id, Name = Name, Url = Url, Transport = Transport, Command = Command, Args = Args.ToList(), WorkDir = WorkDir,
        Enabled = Enabled, Ask = Ask, TimeoutSec = TimeoutSec,
        DisabledTools = DisabledTools.ToList(), Headers = Headers, Env = Env
    };
}

public record McpToolInfo(string Name, string Description, JsonObject Schema);

public class McpException : Exception
{
    public McpException(string message) : base(message) { }
}

/// <summary>How messages travel to one server (HTTP or a local process). id 0 = notification (no reply).</summary>
public abstract class McpTransport : IDisposable
{
    public string Version = McpClient.ProtocolVersion;
    public abstract Task<JsonNode?> SendAsync(string method, JsonObject? p, int id, CancellationToken ct);
    public virtual void Reset() { }
    public virtual void Dispose() { }
}

/// <summary>The server forgot our session (HTTP 404): the client starts again once.</summary>
public class McpSessionExpiredException : McpException
{
    public McpSessionExpiredException() : base("The MCP session expired.") { }
}

/// <summary>Streamable HTTP transport.</summary>
public sealed class McpHttpTransport : McpTransport
{
    readonly HttpClient _http;
    readonly McpServerConfig _cfg;
    string? _session;

    public McpHttpTransport(HttpClient http, McpServerConfig cfg) { _http = http; _cfg = cfg; }

    public override void Reset() => _session = null;

    public override async Task<JsonNode?> SendAsync(string method, JsonObject? p, int id, CancellationToken ct)
    {
        var notification = id == 0;
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (!notification) body["id"] = id;
        if (p != null) body["params"] = p.DeepClone();

        using var req = new HttpRequestMessage(HttpMethod.Post, _cfg.Url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (method != "initialize") req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", Version);
        if (_session != null) req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _session);
        foreach (var (n, v) in McpClient.ParseHeaders(_cfg.Headers)) req.Headers.TryAddWithoutValidation(n, v);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.Headers.TryGetValues("Mcp-Session-Id", out var sid)) _session = sid.FirstOrDefault();

        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound && _session != null && method != "initialize")
        {
            _session = null;
            throw new McpSessionExpiredException();
        }
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            if (err.Length > 300) err = err[..300];
            throw new McpException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {err}".Trim());
        }
        if (notification) return null;

        var ctype = resp.Content.Headers.ContentType?.MediaType ?? "";
        JsonNode? msg;
        if (ctype.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
            msg = await ReadSseAsync(resp, id, ct);
        else
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            msg = McpClient.PickResponse(McpClient.ParseJson(text), id);
        }
        return msg;
    }

    static async Task<JsonNode?> ReadSseAsync(HttpResponseMessage resp, int id, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var data = new StringBuilder();
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    JsonNode? node = null;
                    try { node = JsonNode.Parse(data.ToString()); } catch { }
                    data.Clear();
                    var hit = McpClient.PickResponse(node, id);
                    if (hit != null) return hit;
                }
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line[5..].TrimStart());
            }
        }
        if (data.Length > 0)
        {
            try { return McpClient.PickResponse(JsonNode.Parse(data.ToString()), id); } catch { }
        }
        return null;
    }
}

/// <summary>
/// stdio transport: JSON messages, one per line, over the standard input / output of a local program.
/// The streams come from <paramref name="start"/> (a real process in the app, pipes in the tests).
/// </summary>
public sealed class McpStdioTransport : McpTransport
{
    public sealed record Running(Stream Stdin, Stream Stdout, Func<string> StderrTail, Action Kill);

    readonly Func<Running> _start;
    Running? _run;
    StreamWriter? _writer;
    readonly Dictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    readonly object _gate = new();
    readonly SemaphoreSlim _writeLock = new(1, 1);
    bool _dead;

    public McpStdioTransport(Func<Running> start) => _start = start;

    void EnsureStarted()
    {
        lock (_gate)
        {
            if (_run != null && !_dead) return;
            Stop();
            _run = _start();
            _dead = false;
            _writer = new StreamWriter(_run.Stdin, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            var run = _run;
            _ = Task.Run(() => ReadLoopAsync(run));
        }
    }

    async Task ReadLoopAsync(Running run)
    {
        try
        {
            using var reader = new StreamReader(run.Stdout, Encoding.UTF8);
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                line = line.Trim();
                if (line.Length == 0 || line[0] != '{') continue;   // programs may print other text
                JsonNode? node;
                try { node = JsonNode.Parse(line); } catch { continue; }
                if (node is not JsonObject o) continue;

                var hasId = o["id"] is JsonValue idv && idv.TryGetValue<int>(out _);
                if (o["method"] != null && o["id"] != null)
                {
                    // a request from the server: we support nothing but ping
                    var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = o["id"]!.DeepClone() };
                    if (o["method"]!.ToString() == "ping") reply["result"] = new JsonObject();
                    else reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not supported" };
                    _ = WriteAsync(reply.ToJsonString(), CancellationToken.None);
                    continue;
                }
                if (hasId && (o["result"] != null || o["error"] != null))
                {
                    var id = o["id"]!.GetValue<int>();
                    TaskCompletionSource<JsonNode?>? tcs;
                    lock (_gate) { _pending.Remove(id, out tcs); }
                    tcs?.TrySetResult(o);
                }
            }
        }
        catch { /* the stream was closed */ }
        finally
        {
            List<TaskCompletionSource<JsonNode?>> rest;
            lock (_gate)
            {
                if (ReferenceEquals(_run, run)) _dead = true;
                rest = _pending.Values.ToList();
                _pending.Clear();
            }
            var tail = "";
            try { tail = run.StderrTail().Trim(); } catch { }
            var msg = "The program stopped." + (tail.Length > 0 ? " " + (tail.Length > 400 ? tail[^400..] : tail) : "");
            foreach (var t in rest) t.TrySetException(new McpException(msg));
        }
    }

    async Task WriteAsync(string line, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            if (_writer == null) throw new McpException("The program is not running.");
            await _writer.WriteLineAsync(line.AsMemory(), ct);
        }
        finally { _writeLock.Release(); }
    }

    public override async Task<JsonNode?> SendAsync(string method, JsonObject? p, int id, CancellationToken ct)
    {
        EnsureStarted();
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (id != 0) body["id"] = id;
        if (p != null) body["params"] = p.DeepClone();

        if (id == 0)
        {
            await WriteAsync(body.ToJsonString(), ct);
            return null;
        }

        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) { _pending[id] = tcs; }
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        try
        {
            try { await WriteAsync(body.ToJsonString(), ct); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            { throw new McpException("The program is not running."); }
            return await tcs.Task;
        }
        finally { lock (_gate) { _pending.Remove(id); } }
    }

    void Stop()
    {
        var run = _run;
        _run = null;
        _writer = null;
        if (run == null) return;
        try { run.Stdin.Dispose(); } catch { }
        try { run.Kill(); } catch { }
    }

    public override void Reset() { lock (_gate) Stop(); }
    public override void Dispose() { lock (_gate) Stop(); }
}

/// <summary>Minimal MCP client: initialize, tools/list, tools/call over a transport (HTTP or stdio).</summary>
public class McpClient
{
    public const string ProtocolVersion = "2025-06-18";

    readonly McpTransport _transport;
    readonly McpServerConfig _cfg;
    bool _ready;
    int _id;
    readonly SemaphoreSlim _lock = new(1, 1);

    public McpClient(HttpClient http, McpServerConfig cfg) : this(new McpHttpTransport(http, cfg), cfg) { }

    public McpClient(McpTransport transport, McpServerConfig cfg) { _transport = transport; _cfg = cfg; }

    public string ServerName { get; private set; } = "";

    public static List<(string Name, string Value)> ParseHeaders(string? text)
    {
        var list = new List<(string, string)>();
        foreach (var raw in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = raw.IndexOf(':');
            if (i <= 0) continue;
            list.Add((raw[..i].Trim(), raw[(i + 1)..].Trim()));
        }
        return list;
    }

    CancellationTokenSource Timeout(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_cfg.TimeoutSec, 3, 300)));
        return cts;
    }

    public async Task EnsureAsync(CancellationToken ct)
    {
        if (_ready) return;
        await _lock.WaitAsync(ct);
        try
        {
            if (_ready) return;
            var init = new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "AiChat", ["version"] = "1.0" }
            };
            var res = await RawAsync("initialize", init, false, ct);
            if (res?["protocolVersion"] is JsonValue pv && pv.TryGetValue<string>(out var v) && v.Length > 0) _transport.Version = v;
            ServerName = res?["serverInfo"]?["name"]?.ToString() ?? "";
            await RawAsync("notifications/initialized", null, true, ct);
            _ready = true;
        }
        finally { _lock.Release(); }
    }

    public void Reset() { _ready = false; _transport.Reset(); }

    /// <summary>Stops a local program (does nothing for HTTP).</summary>
    public void Close() { _ready = false; _transport.Dispose(); }

    public async Task<List<McpToolInfo>> ListToolsAsync(CancellationToken ct)
    {
        using var cts = Timeout(ct);
        try
        {
            await EnsureAsync(cts.Token);
            var list = new List<McpToolInfo>();
            string? cursor = null;
            for (var page = 0; page < 20; page++)
            {
                var p = new JsonObject();
                if (cursor != null) p["cursor"] = cursor;
                var res = await RpcAsync("tools/list", p, cts.Token);
                foreach (var t in res?["tools"] as JsonArray ?? new JsonArray())
                {
                    if (t is not JsonObject o) continue;
                    var name = o["name"]?.ToString() ?? "";
                    if (name.Length == 0) continue;
                    var schema = o["inputSchema"] as JsonObject ?? new JsonObject();
                    list.Add(new McpToolInfo(name, o["description"]?.ToString() ?? o["title"]?.ToString() ?? "", McpSchema.Clean(schema)));
                }
                cursor = res?["nextCursor"]?.ToString();
                if (string.IsNullOrEmpty(cursor)) break;
            }
            return list;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new McpException("Timed out."); }
    }

    public async Task<string> CallToolAsync(string name, JsonObject args, CancellationToken ct)
    {
        using var cts = Timeout(ct);
        try
        {
            await EnsureAsync(cts.Token);
            var res = await RpcAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = args.DeepClone() }, cts.Token);
            return FormatResult(res);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new McpException("Timed out."); }
    }

    /// <summary>A request after initialize; an expired HTTP session is restarted once.</summary>
    async Task<JsonNode?> RpcAsync(string method, JsonObject? p, CancellationToken ct)
    {
        try { return await RawAsync(method, p, false, ct); }
        catch (McpSessionExpiredException)
        {
            Reset();
            await EnsureAsync(ct);
            return await RawAsync(method, p, false, ct);
        }
    }

    async Task<JsonNode?> RawAsync(string method, JsonObject? p, bool notification, CancellationToken ct)
    {
        var id = notification ? 0 : Interlocked.Increment(ref _id);
        var msg = await _transport.SendAsync(method, p, id, ct);
        if (notification) return null;
        if (msg == null) throw new McpException("No response from the server.");
        if (msg["error"] is JsonObject e)
            throw new McpException((e["message"]?.ToString() ?? "error") + (e["code"] != null ? $" (code {e["code"]})" : ""));
        return msg["result"];
    }

    /// <summary>Joins the text parts of a tools/call result.</summary>
    public static string FormatResult(JsonNode? res)
    {
        var sb = new StringBuilder();
        foreach (var part in res?["content"] as JsonArray ?? new JsonArray())
        {
            var type = part?["type"]?.ToString();
            switch (type)
            {
                case "text": sb.AppendLine(part?["text"]?.ToString()); break;
                case "image": sb.AppendLine("[image: " + (part?["mimeType"]?.ToString() ?? "") + "]"); break;
                case "audio": sb.AppendLine("[audio: " + (part?["mimeType"]?.ToString() ?? "") + "]"); break;
                case "resource":
                    var r = part?["resource"];
                    sb.AppendLine(r?["text"]?.ToString() ?? "[resource: " + (r?["uri"]?.ToString() ?? "") + "]");
                    break;
                case "resource_link": sb.AppendLine("[link: " + (part?["uri"]?.ToString() ?? "") + "]"); break;
                default: if (part != null) sb.AppendLine(part.ToJsonString()); break;
            }
        }
        var text = sb.ToString().Trim();
        if (text.Length == 0 && res?["structuredContent"] != null) text = res["structuredContent"]!.ToJsonString();
        var isError = res?["isError"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        return isError ? "Error: " + text : text;
    }

    internal static JsonNode? ParseJson(string text)
    {
        try { return JsonNode.Parse(text); } catch { throw new McpException("The server did not return valid JSON."); }
    }

    internal static JsonNode? PickResponse(JsonNode? node, int id)
    {
        if (node is JsonArray arr) return arr.FirstOrDefault(x => IsResponseFor(x, id));
        return IsResponseFor(node, id) ? node : null;
    }

    static bool IsResponseFor(JsonNode? n, int id) =>
        n is JsonObject o && (o["result"] != null || o["error"] != null) &&
        o["id"] is JsonValue v && ((v.TryGetValue<int>(out var i) && i == id) || (v.TryGetValue<string>(out var s) && s == id.ToString()));
}

/// <summary>Makes MCP tool schemas acceptable for the providers.</summary>
public static class McpSchema
{
    static readonly HashSet<string> GeminiKeys = new()
    {
        "type", "description", "properties", "required", "items", "enum", "nullable",
        "minimum", "maximum", "minItems", "maxItems", "anyOf", "format", "title"
    };

    /// <summary>Common clean-up: no $schema / additionalProperties, root is an object with properties.</summary>
    public static JsonObject Clean(JsonObject schema)
    {
        var c = (JsonObject)schema.DeepClone();
        Strip(c, new HashSet<string> { "$schema", "additionalProperties" });
        c["type"] = "object";
        if (c["properties"] is not JsonObject) c["properties"] = new JsonObject();
        return c;
    }

    /// <summary>Gemini accepts only a subset of JSON schema.</summary>
    public static JsonObject ForGemini(JsonObject schema)
    {
        var c = (JsonObject)schema.DeepClone();
        Restrict(c);
        return c;
    }

    static void Strip(JsonNode? n, HashSet<string> keys)
    {
        if (n is JsonObject o)
        {
            foreach (var k in o.Select(x => x.Key).ToList())
            {
                if (keys.Contains(k)) o.Remove(k);
                else Strip(o[k], keys);
            }
        }
        else if (n is JsonArray a) foreach (var x in a) Strip(x, keys);
    }

    static void Restrict(JsonNode? n)
    {
        if (n is JsonObject o)
        {
            // "type": ["string","null"] -> "string" + nullable
            if (o["type"] is JsonArray ta)
            {
                var names = ta.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList();
                var first = names.FirstOrDefault(x => x != "null") ?? "string";
                o["type"] = first;
                if (names.Contains("null")) o["nullable"] = true;
            }
            // o is always a schema here (never the "properties" map), so unknown keywords can go
            foreach (var k in o.Select(x => x.Key).ToList())
                if (!GeminiKeys.Contains(k)) o.Remove(k);
            if (o["properties"] is JsonObject props)
                foreach (var kv in props.ToList()) Restrict(kv.Value);
            Restrict(o["items"]);
            if (o["anyOf"] is JsonArray any) foreach (var x in any) Restrict(x);
            if (o["format"] != null && o["type"]?.ToString() != "string") o.Remove("format");
            if (o["format"] != null && o["format"]!.ToString() is not ("enum" or "date-time")) o.Remove("format");
        }
    }
}

public static class McpToolBuilder
{
    static readonly Regex Bad = new(@"[^a-zA-Z0-9_]+", RegexOptions.Compiled);

    public static string Slug(string s)
    {
        var r = Bad.Replace(s.Trim(), "_").Trim('_');
        return r.Length == 0 ? "server" : (r.Length > 20 ? r[..20] : r);
    }

    /// <summary>Provider-safe unique tool name: mcp_{server}_{tool}, at most 64 characters.</summary>
    public static string ToolName(string server, string tool, ISet<string> used)
    {
        var baseName = "mcp_" + Slug(server) + "_" + Bad.Replace(tool, "_").Trim('_');
        if (baseName.Length > 64) baseName = baseName[..64];
        var name = baseName;
        var n = 2;
        while (!used.Add(name))
        {
            var suffix = "_" + n++;
            name = baseName[..Math.Min(baseName.Length, 64 - suffix.Length)] + suffix;
        }
        return name;
    }

    /// <summary>Tool definitions of one server (the disabled tools are left out).</summary>
    public static List<ToolDef> Build(McpServerConfig cfg, IEnumerable<McpToolInfo> infos, McpClient client, ISet<string> usedNames)
    {
        var list = new List<ToolDef>();
        foreach (var t in infos)
        {
            if (cfg.DisabledTools.Contains(t.Name)) continue;
            var original = t.Name;
            list.Add(new ToolDef
            {
                Name = ToolName(cfg.Name, t.Name, usedNames),
                Label = cfg.Name + ": " + t.Name,
                Description = t.Description.Length > 0 ? t.Description : t.Name,
                Parameters = (JsonObject)t.Schema.DeepClone(),
                Ask = cfg.Ask,
                Source = "mcp",
                Run = (args, ct) => client.CallToolAsync(original, args, ct)
            });
        }
        return list;
    }

    /// <summary>
    /// Reads the common client config format: { "mcpServers": { "name": { "url": "...", "headers": {...} } } }.
    /// Programs ("command", "args", "env") are imported too when <paramref name="allowStdio"/> is true, but switched OFF
    /// so the user reviews them first. Entries that cannot be used go to <paramref name="skipped"/>.
    /// </summary>
    public static List<McpServerConfig> ImportJson(string json, out List<string> skipped, bool allowStdio = true)
    {
        skipped = new();
        var res = new List<McpServerConfig>();
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var servers = (root?["mcpServers"] ?? root?["servers"] ?? root) as JsonObject
                      ?? throw new McpException("Expected an object with \"mcpServers\".");
        foreach (var kv in servers)
        {
            if (kv.Value is not JsonObject o) continue;
            var url = o["url"]?.ToString() ?? o["serverUrl"]?.ToString() ?? "";
            var command = o["command"]?.ToString() ?? "";
            if (url.Length > 0)
            {
                var headers = new StringBuilder();
                if (o["headers"] is JsonObject h)
                    foreach (var hv in h) headers.AppendLine(hv.Key + ": " + hv.Value);
                res.Add(new McpServerConfig { Name = kv.Key, Url = url, Headers = headers.ToString().Trim() });
            }
            else if (command.Length > 0 && allowStdio)
            {
                var env = new StringBuilder();
                if (o["env"] is JsonObject e)
                    foreach (var ev in e) env.AppendLine(ev.Key + "=" + ev.Value);
                res.Add(new McpServerConfig
                {
                    Name = kv.Key, Transport = "stdio", Command = command,
                    Args = (o["args"] as JsonArray)?.Select(x => x?.ToString() ?? "").ToList() ?? new(),
                    WorkDir = o["cwd"]?.ToString() ?? "",
                    Env = env.ToString().Trim(),
                    Enabled = false
                });
            }
            else skipped.Add(kv.Key);
        }
        return res;
    }
}

/// <summary>Starts a local MCP program (Windows only) and keeps it from outliving the app.</summary>
public static class McpProcess
{
    public static bool Supported => OperatingSystem.IsWindows();

    /// <summary>The command line for "cmd /c": programs such as npx are .cmd files that need the shell.</summary>
    public static string BuildCommandLine(string command, IEnumerable<string> args) =>
        string.Join(" ", new[] { Quote(command) }.Concat(args.Select(Quote)));

    static string Quote(string s)
    {
        if (s.Length == 0) return "\"\"";
        return s.IndexOfAny(new[] { ' ', '\t', '"', '&', '|', '<', '>', '^', '(', ')' }) >= 0
            ? "\"" + s.Replace("\"", "\\\"") + "\""
            : s;
    }

    public static List<(string Key, string Value)> ParseEnv(string? text)
    {
        var list = new List<(string, string)>();
        foreach (var raw in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = raw.IndexOf('=');
            if (i <= 0) continue;
            list.Add((raw[..i].Trim(), raw[(i + 1)..].Trim()));
        }
        return list;
    }

    public static McpStdioTransport.Running Start(McpServerConfig cfg)
    {
        if (!Supported) throw new McpException("Local programs are supported on Windows only.");
        if (string.IsNullOrWhiteSpace(cfg.Command)) throw new McpException("No program was set.");

        var psi = new ProcessStartInfo("cmd.exe")
        {
            Arguments = "/d /s /c \"" + BuildCommandLine(cfg.Command.Trim(), cfg.Args.Where(a => a.Length > 0)) + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        if (cfg.WorkDir.Length > 0 && Directory.Exists(cfg.WorkDir)) psi.WorkingDirectory = cfg.WorkDir;
        foreach (var (k, v) in ParseEnv(cfg.Env)) psi.Environment[k] = v;

        var err = new StringBuilder();
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (err) { err.AppendLine(e.Data); if (err.Length > 4000) err.Remove(0, err.Length - 4000); }
        };
        try { proc.Start(); }
        catch (Exception ex) { throw new McpException("Could not start the program: " + ex.Message); }
        proc.BeginErrorReadLine();
        JobObject.Add(proc);

        return new McpStdioTransport.Running(
            proc.StandardInput.BaseStream,
            proc.StandardOutput.BaseStream,
            () => { lock (err) return err.ToString(); },
            () =>
            {
                try { if (!proc.HasExited) proc.Kill(true); } catch { }
                try { proc.Dispose(); } catch { }
            });
    }
}

/// <summary>A Windows job object: the programs we start are killed when the app closes (even if it crashes).</summary>
static class JobObject
{
    static IntPtr _job;
    static bool _failed;

    public static void Add(Process p)
    {
        if (!OperatingSystem.IsWindows() || _failed) return;
        try
        {
            if (_job == IntPtr.Zero)
            {
                _job = CreateJobObject(IntPtr.Zero, null);
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = 0x2000;   // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                var ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    if (!SetInformationJobObject(_job, 9, ptr, (uint)size)) { _failed = true; return; }
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }
            AssignProcessToJobObject(_job, p.Handle);
        }
        catch { _failed = true; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attrs, string? name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, IntPtr info, uint size);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit; public long PerJobUserTimeLimit; public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize; public UIntPtr MaximumWorkingSetSize; public uint ActiveProcessLimit;
        public UIntPtr Affinity; public uint PriorityClass; public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
}
