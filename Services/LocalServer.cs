using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AiChat.Models;

namespace AiChat.Services;

/// <summary>Settings of the local server (AiChat as an OpenAI-compatible API and as an MCP server). Off by default.</summary>
public class LocalServerOptions
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 8765;
    /// <summary>/v1/chat/completions and /v1/models (OpenAI-compatible).</summary>
    public bool Api { get; set; } = true;
    /// <summary>/mcp (Model Context Protocol, Streamable HTTP).</summary>
    public bool Mcp { get; set; } = true;
    /// <summary>Remove the model's &lt;think&gt; part from answers sent to other programs.</summary>
    public bool StripThinking { get; set; } = true;
    public bool LogRequests { get; set; } = true;
    /// <summary>"Provider/model" used when a client sends no model (or "aichat" / "default").</summary>
    public string DefaultModel { get; set; } = "";

    // MCP tools offered to other programs
    public bool ToolAsk { get; set; } = true;
    public bool ToolModels { get; set; } = true;
    /// <summary>list / search / read ALL chats of the app (private, so off by default).</summary>
    public bool ToolChats { get; set; }
    /// <summary>list projects and search their knowledge base.</summary>
    public bool ToolProjects { get; set; }

    /// <summary>Kept in secure storage, not in this JSON.</summary>
    [JsonIgnore] public string Token { get; set; } = "";
}

/// <summary>Removes &lt;think&gt;…&lt;/think&gt; from a stream of text (tags may be split between chunks).</summary>
public sealed class ThinkStripper
{
    const string Open = "<think>", Close = "</think>";
    bool _in, _skipWs;
    string _buf = "";

    public string Feed(string s)
    {
        var text = _buf + s;
        _buf = "";
        var sb = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            if (_in)
            {
                var j = text.IndexOf(Close, i, StringComparison.Ordinal);
                if (j < 0) { _buf = text[^Partial(text, i, Close)..]; return sb.ToString(); }
                i = j + Close.Length;
                _in = false;
                _skipWs = true;
                continue;
            }
            if (_skipWs)
            {
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (i >= text.Length) break;
                _skipWs = false;
            }
            var k = text.IndexOf(Open, i, StringComparison.Ordinal);
            if (k < 0)
            {
                var keep = Partial(text, i, Open);
                sb.Append(text, i, text.Length - i - keep);
                _buf = text[^keep..];
                return sb.ToString();
            }
            sb.Append(text, i, k - i);
            i = k + Open.Length;
            _in = true;
        }
        return sb.ToString();
    }

    /// <summary>Text held back because it could be the start of a tag; call at the end of the stream.</summary>
    public string Flush()
    {
        var r = _in ? "" : _buf;
        _buf = "";
        return r;
    }

    static int Partial(string t, int from, string tag)
    {
        for (var n = Math.Min(tag.Length - 1, t.Length - from); n > 0; n--)
            if (t.AsSpan(t.Length - n).SequenceEqual(tag.AsSpan(0, n))) return n;
        return 0;
    }

    public static string StripAll(string text)
    {
        var s = new ThinkStripper();
        return (s.Feed(text) + s.Flush()).Trim();
    }
}

public record ParsedChat(string Model, bool Stream, string System, List<ChatMessage> Messages, string? Error);

/// <summary>Pure helpers of the OpenAI-compatible API (no network, so they can be tested).</summary>
public static class LocalApi
{
    public static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return "aichat-" + Convert.ToBase64String(bytes).Replace('+', 'a').Replace('/', 'b').TrimEnd('=');
    }

    /// <summary>"Authorization: Bearer x" or "x-api-key: x" must match the token (compared in constant time).</summary>
    public static bool Authorized(string? authorization, string? apiKey, string token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var given = apiKey;
        if (!string.IsNullOrEmpty(authorization))
        {
            var a = authorization.Trim();
            given = a.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? a[7..].Trim() : a;
        }
        if (string.IsNullOrEmpty(given)) return false;
        var x = Encoding.UTF8.GetBytes(given);
        var y = Encoding.UTF8.GetBytes(token);
        return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y);
    }

    /// <summary>Only requests addressed to this PC are served (protects against DNS-rebinding from web pages).</summary>
    public static bool HostOk(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var h = host.Trim().ToLowerInvariant();
        if (h.StartsWith('[')) { var e = h.IndexOf(']'); h = e > 0 ? h[..(e + 1)] : h; }
        else { var c = h.LastIndexOf(':'); if (c > 0) h = h[..c]; }
        return h is "localhost" or "127.0.0.1" or "[::1]";
    }

    static string TextOf(JsonNode? c)
    {
        if (c is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        if (c is JsonArray arr)
        {
            var parts = new List<string>();
            foreach (var p in arr)
            {
                var type = p?["type"]?.ToString();
                if (type is "text" or "input_text" or null) { var t = p?["text"]?.ToString(); if (!string.IsNullOrEmpty(t)) parts.Add(t); }
                else if (type is "image_url" or "input_image") parts.Add("[image omitted]");
            }
            return string.Join("\n", parts);
        }
        return c?.ToString() ?? "";
    }

    public static ParsedChat Parse(JsonObject body)
    {
        var model = body["model"]?.ToString() ?? "";
        var stream = body["stream"] is JsonValue sv && sv.TryGetValue<bool>(out var b) && b;
        if (body["messages"] is not JsonArray msgs || msgs.Count == 0)
            return new ParsedChat(model, stream, "", new(), "\"messages\" is required.");
        var system = new List<string>();
        var list = new List<ChatMessage>();
        foreach (var m in msgs)
        {
            var role = m?["role"]?.ToString() ?? "user";
            var text = TextOf(m?["content"]);
            switch (role)
            {
                case "system":
                case "developer":
                    if (text.Length > 0) system.Add(text);
                    break;
                case "assistant":
                    if (text.Length > 0) list.Add(new ChatMessage { Role = "assistant", Content = text });
                    break;
                case "tool":
                case "function":
                    list.Add(new ChatMessage { Role = "user", Content = "[tool result]\n" + text });
                    break;
                default:
                    list.Add(new ChatMessage { Role = "user", Content = text });
                    break;
            }
        }
        if (!list.Any(x => x.Role == "user")) return new ParsedChat(model, stream, "", new(), "At least one user message is required.");
        return new ParsedChat(model, stream, string.Join("\n\n", system), list, null);
    }

    public static IEnumerable<ProviderConfig> ChatProviders(IEnumerable<ProviderConfig> all) => all.Where(p => !p.IsGenerator);

    /// <summary>All models as "Provider/model".</summary>
    public static List<string> ModelIds(IEnumerable<ProviderConfig> all)
    {
        var res = new List<string>();
        foreach (var p in ChatProviders(all))
        {
            var models = p.Models.ToList();
            if (!string.IsNullOrWhiteSpace(p.DefaultModel) && !models.Contains(p.DefaultModel)) models.Insert(0, p.DefaultModel);
            res.AddRange(models.Select(m => p.Name + "/" + m));
        }
        return res;
    }

    /// <summary>Finds the API and model for what a client asked ("Provider/model", a model name, or nothing = the default).</summary>
    public static (ProviderConfig? Provider, string Model, string? Error) Resolve(IEnumerable<ProviderConfig> all, string? requested, string defaultSpec)
    {
        var chat = ChatProviders(all).ToList();
        if (chat.Count == 0) return (null, "", "No chat API is configured in AiChat.");
        var spec = (requested ?? "").Trim();
        if (spec.Length == 0 || spec.Equals("aichat", StringComparison.OrdinalIgnoreCase) || spec.Equals("default", StringComparison.OrdinalIgnoreCase))
            spec = (defaultSpec ?? "").Trim();
        if (spec.Length == 0)
        {
            var p0 = chat.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.DefaultModel));
            return p0 == null ? (null, "", "No default model: send \"model\": \"Provider/model\".") : (p0, p0.DefaultModel, null);
        }
        // "Provider/model" (longest provider name first)
        foreach (var p in chat.OrderByDescending(p => p.Name.Length))
            if (spec.Length > p.Name.Length + 1 && spec.StartsWith(p.Name + "/", StringComparison.OrdinalIgnoreCase))
                return (p, spec[(p.Name.Length + 1)..], null);
        var byList = chat.FirstOrDefault(p => p.Models.Contains(spec, StringComparer.OrdinalIgnoreCase));
        if (byList != null) return (byList, byList.Models.First(m => m.Equals(spec, StringComparison.OrdinalIgnoreCase)), null);
        var byDefault = chat.FirstOrDefault(p => p.DefaultModel.Equals(spec, StringComparison.OrdinalIgnoreCase));
        if (byDefault != null) return (byDefault, byDefault.DefaultModel, null);
        if (chat.Count == 1) return (chat[0], spec, null);
        return (null, "", $"Unknown model '{spec}'. Use \"Provider/model\" (GET /v1/models lists them).");
    }

    // ---- JSON bodies ----

    static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public static JsonObject Completion(string id, string model, string content) => new()
    {
        ["id"] = id, ["object"] = "chat.completion", ["created"] = Now(), ["model"] = model,
        ["choices"] = new JsonArray(new JsonObject
        {
            ["index"] = 0,
            ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
            ["finish_reason"] = "stop"
        }),
        ["usage"] = new JsonObject { ["prompt_tokens"] = 0, ["completion_tokens"] = 0, ["total_tokens"] = 0 }
    };

    /// <summary>One streaming chunk. delta = null and finish = "stop" ends the answer; role = true opens it.</summary>
    public static JsonObject Chunk(string id, string model, string? delta, string? finish = null, bool role = false)
    {
        var d = new JsonObject();
        if (role) d["role"] = "assistant";
        if (delta != null) d["content"] = delta;
        return new JsonObject
        {
            ["id"] = id, ["object"] = "chat.completion.chunk", ["created"] = Now(), ["model"] = model,
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = d, ["finish_reason"] = finish })
        };
    }

    public static string Sse(JsonNode n) => "data: " + n.ToJsonString() + "\n\n";

    public static JsonObject ModelsList(IEnumerable<string> ids) => new()
    {
        ["object"] = "list",
        ["data"] = new JsonArray(ids.Select(i => (JsonNode?)new JsonObject { ["id"] = i, ["object"] = "model", ["created"] = 0, ["owned_by"] = "aichat" }).ToArray())
    };

    public static JsonObject Error(string message, string type = "invalid_request_error") =>
        new() { ["error"] = new JsonObject { ["message"] = message, ["type"] = type } };
}

public record McpToolSpec(string Name, string Description, JsonObject Schema);

public interface ILocalMcpHost
{
    IReadOnlyList<McpToolSpec> Tools();
    Task<string> CallAsync(string name, JsonObject args, CancellationToken ct);
}

/// <summary>The JSON-RPC part of an MCP server (tools only).</summary>
public static class LocalMcp
{
    static readonly string[] Supported = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };
    public const string Version = "1.0";

    /// <summary>Handles one request or a batch. Null = nothing to answer (notification).</summary>
    public static async Task<JsonNode?> HandleAsync(JsonNode? req, ILocalMcpHost host, CancellationToken ct)
    {
        if (req is JsonArray batch)
        {
            var res = new JsonArray();
            foreach (var r in batch)
                if (await HandleOneAsync(r as JsonObject, host, ct) is { } one) res.Add(one);
            return res.Count == 0 ? null : res;
        }
        return await HandleOneAsync(req as JsonObject, host, ct);
    }

    static JsonObject Reply(JsonNode? id, JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };

    public static JsonObject ErrorReply(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    static async Task<JsonObject?> HandleOneAsync(JsonObject? req, ILocalMcpHost host, CancellationToken ct)
    {
        if (req == null) return ErrorReply(null, -32600, "Invalid request");
        var method = req["method"]?.ToString() ?? "";
        var id = req["id"];
        if (id == null) return null; // notification (initialized, cancelled, ...)

        switch (method)
        {
            case "initialize":
            {
                var asked = req["params"]?["protocolVersion"]?.ToString();
                var version = asked != null && Supported.Contains(asked) ? asked : Supported[1];
                return Reply(id, new JsonObject
                {
                    ["protocolVersion"] = version,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = "aichat", ["version"] = Version }
                });
            }
            case "ping":
                return Reply(id, new JsonObject());
            case "tools/list":
                return Reply(id, new JsonObject
                {
                    ["tools"] = new JsonArray(host.Tools().Select(t => (JsonNode?)new JsonObject
                    {
                        ["name"] = t.Name, ["description"] = t.Description, ["inputSchema"] = t.Schema.DeepClone()
                    }).ToArray())
                });
            case "tools/call":
            {
                var name = req["params"]?["name"]?.ToString() ?? "";
                if (!host.Tools().Any(t => t.Name == name)) return ErrorReply(id, -32602, "Unknown tool: " + name);
                var args = req["params"]?["arguments"] as JsonObject ?? new JsonObject();
                string text;
                var isError = false;
                try { text = await host.CallAsync(name, args, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { text = "Error: " + ex.Message; isError = true; }
                return Reply(id, new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                    ["isError"] = isError
                });
            }
            case "resources/list": return Reply(id, new JsonObject { ["resources"] = new JsonArray() });
            case "prompts/list": return Reply(id, new JsonObject { ["prompts"] = new JsonArray() });
            default:
                return ErrorReply(id, -32601, "Method not found: " + method);
        }
    }
}

/// <summary>What the HTTP server needs from the app.</summary>
public interface ILocalHost : ILocalMcpHost
{
    IReadOnlyList<ProviderConfig> Providers();
    IAsyncEnumerable<string> StreamAsync(ProviderConfig provider, string model, IReadOnlyList<ChatMessage> history, string? system, CancellationToken ct);
}

/// <summary>A tiny HTTP server on 127.0.0.1 (nothing else can connect). Every call needs the token.</summary>
public sealed class LocalHttpServer : IDisposable
{
    readonly LocalServerOptions _o;
    readonly ILocalHost _host;
    readonly Action<string> _log;
    readonly SemaphoreSlim _gate = new(4, 4);
    HttpListener? _listener;
    CancellationTokenSource? _cts;
    const int MaxBody = 20 * 1024 * 1024;

    public LocalHttpServer(LocalServerOptions options, ILocalHost host, Action<string> log) { _o = options; _host = host; _log = log; }

    public bool Running => _listener is { IsListening: true };

    /// <summary>Starts listening. Returns null or the reason it could not (e.g. the port is in use).</summary>
    public string? Start()
    {
        try
        {
            var l = new HttpListener();
            l.Prefixes.Add($"http://127.0.0.1:{_o.Port}/");
            try { l.Prefixes.Add($"http://localhost:{_o.Port}/"); } catch { }
            l.Start();
            _listener = l;
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => LoopAsync(l, _cts.Token));
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Close(); } catch { }
        _listener = null;
    }

    async Task LoopAsync(HttpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && l.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await l.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(async () =>
            {
                try { await HandleAsync(ctx, ct); }
                catch (Exception ex) { if (!ct.IsCancellationRequested) _log("Local server error: " + ex.Message); }
                finally { try { ctx.Response.Close(); } catch { } }
            }, ct);
        }
    }

    static async Task Json(HttpListenerResponse r, int status, JsonNode body)
    {
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        r.StatusCode = status;
        r.ContentType = "application/json; charset=utf-8";
        r.ContentLength64 = bytes.Length;
        await r.OutputStream.WriteAsync(bytes);
    }

    async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        var path = req.Url?.AbsolutePath.TrimEnd('/') ?? "";
        if (path.Length == 0) path = "/";
        var status = 200;
        try
        {
            if (!LocalApi.HostOk(req.Headers["Host"]) || req.Headers["Origin"] != null)
            {
                status = 403;
                await Json(res, 403, LocalApi.Error("Forbidden", "forbidden"));
                return;
            }
            if (path is "/" or "/health")
            {
                await Json(res, 200, new JsonObject { ["ok"] = true, ["name"] = "AiChat" });
                return;
            }
            if (!LocalApi.Authorized(req.Headers["Authorization"], req.Headers["x-api-key"], _o.Token))
            {
                status = 401;
                res.AddHeader("WWW-Authenticate", "Bearer");
                await Json(res, 401, LocalApi.Error("Invalid or missing token.", "authentication_error"));
                return;
            }

            if (_o.Api && path == "/v1/models" && req.HttpMethod == "GET")
            {
                await Json(res, 200, LocalApi.ModelsList(LocalApi.ModelIds(_host.Providers())));
                return;
            }
            if (_o.Api && path == "/v1/chat/completions" && req.HttpMethod == "POST")
            {
                status = await ChatAsync(req, res, ct);
                return;
            }
            if (_o.Mcp && path == "/mcp")
            {
                if (req.HttpMethod == "POST")
                {
                    var text = await ReadBodyAsync(req);
                    JsonNode? parsed;
                    try { parsed = JsonNode.Parse(text); }
                    catch (JsonException) { status = 400; await Json(res, 400, LocalMcp.ErrorReply(null, -32700, "Parse error")); return; }
                    using var tc = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    tc.CancelAfter(TimeSpan.FromMinutes(10));
                    var answer = await LocalMcp.HandleAsync(parsed, _host, tc.Token);
                    if (answer == null) { status = 202; res.StatusCode = 202; return; }
                    await Json(res, 200, answer);
                    return;
                }
                status = 405;
                res.AddHeader("Allow", "POST");
                res.StatusCode = 405;
                return;
            }
            status = 404;
            await Json(res, 404, LocalApi.Error("Not found: " + path, "not_found"));
        }
        finally
        {
            if (_o.LogRequests && path != "/health") _log($"Local server: {req.HttpMethod} {path} -> {status}");
        }
    }

    static async Task<string> ReadBodyAsync(HttpListenerRequest req)
    {
        if (req.ContentLength64 > MaxBody) throw new InvalidOperationException("Request too large.");
        using var sr = new StreamReader(req.InputStream, Encoding.UTF8);
        var buf = new char[8192];
        var sb = new StringBuilder();
        int n;
        while ((n = await sr.ReadAsync(buf, 0, buf.Length)) > 0)
        {
            sb.Append(buf, 0, n);
            if (sb.Length > MaxBody) throw new InvalidOperationException("Request too large.");
        }
        return sb.ToString();
    }

    async Task<int> ChatAsync(HttpListenerRequest req, HttpListenerResponse res, CancellationToken ct)
    {
        JsonObject? body;
        try { body = JsonNode.Parse(await ReadBodyAsync(req)) as JsonObject; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            await Json(res, 400, LocalApi.Error("Invalid JSON body."));
            return 400;
        }
        if (body == null) { await Json(res, 400, LocalApi.Error("Invalid JSON body.")); return 400; }
        var chat = LocalApi.Parse(body);
        if (chat.Error != null) { await Json(res, 400, LocalApi.Error(chat.Error)); return 400; }
        var (provider, model, err) = LocalApi.Resolve(_host.Providers(), chat.Model, _o.DefaultModel);
        if (provider == null) { await Json(res, 404, LocalApi.Error(err ?? "Unknown model.", "model_not_found")); return 404; }

        await _gate.WaitAsync(ct);
        try
        {
            var id = "chatcmpl-" + Guid.NewGuid().ToString("N")[..24];
            var shown = provider.Name + "/" + model;
            var strip = _o.StripThinking ? new ThinkStripper() : null;
            var system = string.IsNullOrWhiteSpace(chat.System) ? null : chat.System;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await using var e = _host.StreamAsync(provider, model, chat.Messages, system, cts.Token).GetAsyncEnumerator(cts.Token);

            // wait for the first piece, so that a failing API still gives a proper HTTP error
            string? first = null;
            bool more;
            try
            {
                more = await e.MoveNextAsync();
                while (more && e.Current.StartsWith(ProviderStatus.Prefix, StringComparison.Ordinal)) more = await e.MoveNextAsync();
                if (more) first = e.Current;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                await Json(res, 502, LocalApi.Error(ProviderHttp.Describe(ex), "upstream_error"));
                return 502;
            }

            if (!chat.Stream)
            {
                var sb = new StringBuilder();
                try
                {
                    while (more)
                    {
                        var piece = e.Current;
                        if (!piece.StartsWith(ProviderStatus.Prefix, StringComparison.Ordinal)) sb.Append(strip != null ? strip.Feed(piece) : piece);
                        more = await e.MoveNextAsync();
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    await Json(res, 502, LocalApi.Error(ProviderHttp.Describe(ex), "upstream_error"));
                    return 502;
                }
                if (strip != null) sb.Append(strip.Flush());
                await Json(res, 200, LocalApi.Completion(id, shown, strip != null ? sb.ToString().Trim() : sb.ToString()));
                return 200;
            }

            res.StatusCode = 200;
            res.ContentType = "text/event-stream; charset=utf-8";
            res.SendChunked = true;
            res.AddHeader("Cache-Control", "no-cache");

            async Task Write(JsonNode n)
            {
                var bytes = Encoding.UTF8.GetBytes(LocalApi.Sse(n));
                await res.OutputStream.WriteAsync(bytes, cts.Token);
                await res.OutputStream.FlushAsync(cts.Token);
            }

            try
            {
                await Write(LocalApi.Chunk(id, shown, "", role: true));
                while (more)
                {
                    var piece = e.Current;
                    if (!piece.StartsWith(ProviderStatus.Prefix, StringComparison.Ordinal))
                    {
                        var t = strip != null ? strip.Feed(piece) : piece;
                        if (t.Length > 0) await Write(LocalApi.Chunk(id, shown, t));
                    }
                    more = await e.MoveNextAsync();
                }
                var tail = strip?.Flush() ?? "";
                if (tail.Length > 0) await Write(LocalApi.Chunk(id, shown, tail));
                await Write(LocalApi.Chunk(id, shown, null, "stop"));
                var done = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
                await res.OutputStream.WriteAsync(done, cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (HttpListenerException) { cts.Cancel(); } // the client went away
            catch (IOException) { cts.Cancel(); }
            catch (Exception ex)
            {
                try { await Write(LocalApi.Error(ProviderHttp.Describe(ex), "upstream_error")); } catch { }
            }
            return 200;
        }
        finally { _gate.Release(); }
    }
}
