using System.Net.Http.Headers;
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
    public bool Enabled { get; set; } = true;
    /// <summary>Ask the user before every tool of this server runs.</summary>
    public bool Ask { get; set; }
    public int TimeoutSec { get; set; } = 30;
    /// <summary>Tools (original names) the user switched off.</summary>
    public List<string> DisabledTools { get; set; } = new();
    /// <summary>One "Name: value" per line (e.g. Authorization: Bearer ...). Not serialized with the list.</summary>
    [JsonIgnore] public string Headers { get; set; } = "";

    public McpServerConfig Copy() => new()
    {
        Id = Id, Name = Name, Url = Url, Enabled = Enabled, Ask = Ask, TimeoutSec = TimeoutSec,
        DisabledTools = DisabledTools.ToList(), Headers = Headers
    };
}

public record McpToolInfo(string Name, string Description, JsonObject Schema);

public class McpException : Exception
{
    public McpException(string message) : base(message) { }
}

/// <summary>Minimal MCP client over Streamable HTTP: initialize, tools/list, tools/call.</summary>
public class McpClient
{
    public const string ProtocolVersion = "2025-06-18";

    readonly HttpClient _http;
    readonly McpServerConfig _cfg;
    string? _session;
    string _version = ProtocolVersion;
    bool _ready;
    int _id;
    readonly SemaphoreSlim _lock = new(1, 1);

    public McpClient(HttpClient http, McpServerConfig cfg) { _http = http; _cfg = cfg; }

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
            var res = await SendAsync("initialize", init, false, ct);
            if (res?["protocolVersion"] is JsonValue pv && pv.TryGetValue<string>(out var v) && v.Length > 0) _version = v;
            ServerName = res?["serverInfo"]?["name"]?.ToString() ?? "";
            await SendAsync("notifications/initialized", null, true, ct);
            _ready = true;
        }
        finally { _lock.Release(); }
    }

    public void Reset() { _ready = false; _session = null; }

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
                var res = await SendAsync("tools/list", p, false, cts.Token);
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
            var res = await SendAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = args.DeepClone() }, false, cts.Token);
            return FormatResult(res);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new McpException("Timed out."); }
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

    async Task<JsonNode?> SendAsync(string method, JsonObject? p, bool notification, CancellationToken ct, bool retried = false)
    {
        var id = notification ? 0 : Interlocked.Increment(ref _id);
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (!notification) body["id"] = id;
        if (p != null) body["params"] = p.DeepClone();

        using var req = new HttpRequestMessage(HttpMethod.Post, _cfg.Url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (method != "initialize") req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _version);
        if (_session != null) req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _session);
        foreach (var (n, v) in ParseHeaders(_cfg.Headers)) req.Headers.TryAddWithoutValidation(n, v);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.Headers.TryGetValues("Mcp-Session-Id", out var sid)) _session = sid.FirstOrDefault();

        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound && _session != null && method != "initialize" && !retried)
        {
            // the session expired: start again once
            Reset();
            await EnsureAsync(ct);
            return await SendAsync(method, p, notification, ct, true);
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
            msg = PickResponse(ParseJson(text), id);
        }
        if (msg == null) throw new McpException("No response from the server.");
        if (msg["error"] is JsonObject e)
            throw new McpException((e["message"]?.ToString() ?? "error") + (e["code"] != null ? $" (code {e["code"]})" : ""));
        return msg["result"];
    }

    static JsonNode? ParseJson(string text)
    {
        try { return JsonNode.Parse(text); } catch { throw new McpException("The server did not return valid JSON."); }
    }

    static JsonNode? PickResponse(JsonNode? node, int id)
    {
        if (node is JsonArray arr) return arr.FirstOrDefault(x => IsResponseFor(x, id));
        return IsResponseFor(node, id) ? node : null;
    }

    static bool IsResponseFor(JsonNode? n, int id) =>
        n is JsonObject o && (o["result"] != null || o["error"] != null) &&
        o["id"] is JsonValue v && ((v.TryGetValue<int>(out var i) && i == id) || (v.TryGetValue<string>(out var s) && s == id.ToString()));

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
                    var hit = PickResponse(node, id);
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
            try { return PickResponse(JsonNode.Parse(data.ToString()), id); } catch { }
        }
        return null;
    }
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

    /// <summary>Reads the common client config format: { "mcpServers": { "name": { "url": "...", "headers": {...} } } }.</summary>
    public static List<McpServerConfig> ImportJson(string json, out List<string> skipped)
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
            if (url.Length == 0) { skipped.Add(kv.Key); continue; }
            var headers = new StringBuilder();
            if (o["headers"] is JsonObject h)
                foreach (var hv in h) headers.AppendLine(hv.Key + ": " + hv.Value);
            res.Add(new McpServerConfig { Name = kv.Key, Url = url, Headers = headers.ToString().Trim() });
        }
        return res;
    }
}
