using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AiChat.Models;

namespace AiChat.Services;

/// <summary>Minimal JSONPath: $.a.b[0].c, [*], .*, ['name'].</summary>
public static class JPath
{
    record Seg(string? Name, int? Index, bool Wild);

    static List<Seg> Parse(string path)
    {
        var segs = new List<Seg>();
        var s = (path ?? "").Trim();
        var i = 0;
        if (s.StartsWith('$')) i = 1;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '.')
            {
                i++;
                var start = i;
                while (i < s.Length && s[i] != '.' && s[i] != '[') i++;
                var name = s[start..i];
                if (name.Length == 0) throw new FormatException($"Invalid path: {path}");
                segs.Add(name == "*" ? new Seg(null, null, true) : new Seg(name, null, false));
            }
            else if (c == '[')
            {
                var end = s.IndexOf(']', i);
                if (end < 0) throw new FormatException($"Missing ] in path: {path}");
                var inner = s[(i + 1)..end].Trim();
                i = end + 1;
                if (inner == "*") segs.Add(new Seg(null, null, true));
                else if (int.TryParse(inner, out var idx)) segs.Add(new Seg(null, idx, false));
                else if (inner.Length >= 2 && (inner[0] == '\'' || inner[0] == '"') && inner[^1] == inner[0])
                    segs.Add(new Seg(inner[1..^1], null, false));
                else throw new FormatException($"Invalid index in path: {path}");
            }
            else if (i == 0 || (i == 1 && s[0] == '$'))
            {
                // a bare leading name such as "data[0].id"
                var start = i;
                while (i < s.Length && s[i] != '.' && s[i] != '[') i++;
                segs.Add(new Seg(s[start..i], null, false));
            }
            else throw new FormatException($"Invalid path: {path}");
        }
        return segs;
    }

    public static void Check(string path) => Parse(path);

    public static List<JsonNode> Select(JsonNode? root, string path)
    {
        var cur = new List<JsonNode?> { root };
        foreach (var seg in Parse(path))
        {
            var next = new List<JsonNode?>();
            foreach (var n in cur)
            {
                if (n == null) continue;
                if (seg.Wild)
                {
                    if (n is JsonArray a) next.AddRange(a);
                    else if (n is JsonObject o) next.AddRange(o.Select(kv => kv.Value));
                }
                else if (seg.Index is int idx)
                {
                    if (n is JsonArray a)
                    {
                        var k = idx < 0 ? a.Count + idx : idx;
                        if (k >= 0 && k < a.Count) next.Add(a[k]);
                    }
                }
                else if (n is JsonObject o && o.TryGetPropertyValue(seg.Name!, out var v)) next.Add(v);
            }
            cur = next;
        }
        return cur.Where(x => x != null).Select(x => x!).ToList();
    }

    public static string Text(JsonNode? n)
    {
        if (n == null) return "";
        if (n is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        return n.ToJsonString();
    }
}

/// <summary>{{placeholder}} expansion over a JSON tree. Shared by connectors and the extra-body controls.</summary>
public static class Tpl
{
    static readonly Regex Whole = new(@"^\s*\{\{\s*([A-Za-z0-9_.\-|]+)\s*\}\}\s*$", RegexOptions.Compiled);
    static readonly Regex Inline = new(@"\{\{\s*([A-Za-z0-9_.\-|]+)\s*\}\}", RegexOptions.Compiled);

    static bool IsPlaceholder(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) && Whole.IsMatch(s);
    static bool Droppable(JsonNode n) => IsPlaceholder(n) || (n is JsonObject o && o.ContainsKey("$if"));

    static bool Truthy(JsonNode? n)
    {
        if (n == null) return false;
        if (n is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b;
            if (v.TryGetValue<string>(out var s)) return s.Length > 0 && !s.Equals("false", StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }

    /// <summary>
    /// A string that is exactly one placeholder keeps the value's type. A field (or array item) whose placeholder
    /// has no value is removed. An object with "$if": "{{x}}" is removed when x is empty / false / missing.
    /// </summary>
    public static JsonNode? Expand(JsonNode? node, Func<string, JsonNode?> resolve)
    {
        switch (node)
        {
            case null: return null;
            case JsonObject o:
            {
                if (o.TryGetPropertyValue("$if", out var cond) && !Truthy(Expand(cond, resolve))) return null;
                var r = new JsonObject();
                foreach (var (k, v) in o)
                {
                    if (k == "$if") continue;
                    var e = Expand(v, resolve);
                    if (e == null && v != null && Droppable(v)) continue;
                    r[k] = e;
                }
                return r;
            }
            case JsonArray a:
            {
                var r = new JsonArray();
                foreach (var v in a)
                {
                    var e = Expand(v, resolve);
                    if (e == null && v != null && Droppable(v)) continue;
                    r.Add(e);
                }
                return r;
            }
            case JsonValue v when v.TryGetValue<string>(out var s):
            {
                var m = Whole.Match(s);
                if (m.Success) return resolve(m.Groups[1].Value);
                return Inline.Replace(s, mm => JPath.Text(resolve(mm.Groups[1].Value)));
            }
            default:
                return node.DeepClone();
        }
    }
}

/// <summary>One UI field declared by a connector ("inputs").</summary>
public record ConnInput(string Id, string Label, string Type, List<string> Options, string Default, double? Min = null, double? Max = null, double? Step = null);

public static class ConnectorSpec
{
    public static JsonObject? TryParse(string? json, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json)) { error = "The connector is empty."; return null; }
        try
        {
            if (JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is JsonObject o)
                return o;
            error = "The connector must be a JSON object.";
        }
        catch (JsonException ex) { error = "Invalid JSON: " + ex.Message; }
        return null;
    }

    public static List<ConnInput> Inputs(string? json) =>
        ParseInputs(TryParse(json, out _)?["inputs"] as JsonArray);

    /// <summary>The "controls" of a normal (non-connector) API: a JSON array in the same format as connector inputs.</summary>
    public static List<ConnInput> ControlInputs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<ConnInput>();
        try { return ParseInputs(JsonNode.Parse(json) as JsonArray); }
        catch (JsonException) { return new List<ConnInput>(); }
    }

    static double? Num(JsonNode? n) =>
        n != null && double.TryParse(JPath.Text(n), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;

    public static List<ConnInput> ParseInputs(JsonArray? arr)
    {
        var list = new List<ConnInput>();
        if (arr == null) return list;
        foreach (var it in arr)
        {
            if (it is not JsonObject o || JPath.Text(o["id"]) is not { Length: > 0 } id) continue;
            var type = JPath.Text(o["type"]);
            if (type.Length == 0) type = "text";
            var opts = o["options"] is JsonArray oa ? oa.Select(JPath.Text).ToList() : new List<string>();
            list.Add(new ConnInput(id, JPath.Text(o["label"]) is { Length: > 0 } l ? l : id, type, opts, JPath.Text(o["default"]),
                Num(o["min"]), Num(o["max"]), Num(o["step"])));
        }
        return list;
    }

    /// <summary>The typed value of an input (user's value, else the default); null when empty.</summary>
    public static JsonNode? InputValue(List<ConnInput> defs, IReadOnlyDictionary<string, string> values, string id)
    {
        var def = defs.FirstOrDefault(i => i.Id == id);
        string? raw = values.TryGetValue(id, out var set) ? set : def?.Default;
        if (string.IsNullOrEmpty(raw)) return null;
        switch (def?.Type)
        {
            case "number":
            case "slider":
                if (long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var l)) return l;
                if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
                return null;
            case "bool":
                return raw.Equals("true", StringComparison.OrdinalIgnoreCase);
            default:
                return raw;
        }
    }

    /// <summary>Returns an error message, or null when the connector looks usable.</summary>
    public static string? Validate(string? json)
    {
        var root = TryParse(json, out var err);
        if (root == null) return err;
        try
        {
            if (root["request"] is not JsonObject req) return "Missing \"request\" object.";
            if (string.IsNullOrWhiteSpace(JPath.Text(req["path"]))) return "request.path is required.";
            CheckResponse(root["response"]);

            if (root["job"] is JsonObject job)
            {
                if (string.IsNullOrWhiteSpace(JPath.Text(job["id"]))) return "job.id (JSON path of the job id) is required.";
                JPath.Check(JPath.Text(job["id"]));
                if (job["poll"] is not JsonObject poll || string.IsNullOrWhiteSpace(JPath.Text(poll["path"])))
                    return "job.poll.path is required.";
                if (JPath.Text(poll["statusPath"]) is { Length: > 0 } sp) JPath.Check(sp);
                if (JPath.Text(poll["progressPath"]) is { Length: > 0 } pp) JPath.Check(pp);
                if (job["cancel"] != null && (job["cancel"] is not JsonObject co || string.IsNullOrWhiteSpace(JPath.Text(co["path"]))))
                    return "job.cancel.path is required.";
                CheckResponse(job["response"]);
                if (job["response"] == null && root["response"] == null)
                    return "Define \"response\" (or job.response) to say where the result is.";
            }
            else if (JPath.Text(root["kind"]) == "chat")
            {
                var st = root["stream"] as JsonObject;
                var tp = JPath.Text(st?["textPath"]);
                if (tp.Length == 0) tp = JPath.Text(root["response"]?["text"]);
                if (tp.Length == 0) return "A chat connector needs stream.textPath (JSON path of the text in each streamed event).";
                JPath.Check(tp);
                if (JPath.Text(st?["reasoningPath"]) is { Length: > 0 } rp) JPath.Check(rp);
                if (JPath.Text(st?["errorPath"]) is { Length: > 0 } ep) JPath.Check(ep);
                var fmt = JPath.Text(st?["format"]);
                if (fmt.Length > 0 && fmt is not ("sse" or "ndjson" or "none")) return "stream.format must be sse, ndjson or none.";
            }
            else if (root["response"] == null)
                return "Define \"response\" to say where the result is (use {\"type\":\"binary\"} for raw files).";

            if (root["models"] is JsonObject m && JPath.Text(m["list"]) is { Length: > 0 } lp) JPath.Check(lp);
        }
        catch (FormatException ex) { return ex.Message; }
        return null;
    }

    static void CheckResponse(JsonNode? resp)
    {
        if (resp is not JsonObject r) return;
        if (JPath.Text(r["text"]) is { Length: > 0 } t) JPath.Check(t);
        if (r["results"] is JsonArray arr)
            foreach (var it in arr)
                if (it is JsonObject o && JPath.Text(o["from"]) is { Length: > 0 } f) JPath.Check(f);
    }
}

/// <summary>A streamed chunk that starts with this character is a status line (progress), not message text.</summary>
public static class ProviderStatus
{
    public const string Prefix = "\u0001";
}

public class ConnectorProvider : IChatProvider
{
    readonly HttpClient _http;
    public ConnectorProvider(HttpClient http) => _http = http;

    public async IAsyncEnumerable<string> StreamAsync(ProviderConfig cfg, string model,
        IReadOnlyList<ChatMessage> history, string? systemPrompt, Func<string, byte[], Task>? onImage,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var run = new ConnectorRun(_http, cfg, model, history, systemPrompt, onImage, ct);
        if (run.IsChat)
        {
            await foreach (var t in run.ChatStreamAsync()) yield return t;
            yield break;
        }

        // Status lines (progress of an async job) are streamed while the job runs.
        var channel = System.Threading.Channels.Channel.CreateUnbounded<string>();
        run.OnStatus = s => channel.Writer.TryWrite(s);
        var work = Task.Run(async () =>
        {
            try { await run.ExecuteAsync(); }
            finally { channel.Writer.Complete(); }
        });
        await foreach (var status in channel.Reader.ReadAllAsync(ct))
            yield return ProviderStatus.Prefix + status;
        await work;
        foreach (var t in run.Texts) yield return t;
    }

    public async Task<List<string>> ListModelsAsync(ProviderConfig cfg, CancellationToken ct)
    {
        var root = ConnectorSpec.TryParse(cfg.ConnectorJson, out var err) ?? throw new InvalidOperationException(err);
        if (root["models"] is not JsonObject m) return new List<string>();

        if (m["static"] is JsonArray st)
            return st.Select(JPath.Text).Where(x => x.Length > 0).ToList();

        var run = new ConnectorRun(_http, cfg, "", new List<ChatMessage>(), null, null, ct);
        var node = await run.FetchJsonAsync(m);
        var listPath = JPath.Text(m["list"]);
        var items = JPath.Select(node, listPath.Length > 0 ? listPath : "$[*]");
        return items.Select(i => i is JsonObject o ? JPath.Text(o["id"] ?? o["name"]) : JPath.Text(i))
            .Where(x => x.Length > 0).Distinct().ToList();
    }
}

sealed class ConnectorRun
{
    static readonly Regex ImgRef = new(@"^image(\d*)_(base64|dataurl|mime)$", RegexOptions.Compiled);

    readonly HttpClient _http;
    readonly ProviderConfig _cfg;
    readonly JsonObject _spec;
    readonly Func<string, byte[], Task>? _onImage;
    readonly CancellationToken _ct;

    readonly string _prompt, _model, _system;
    readonly List<(string B64, string Mime)> _imgs = new();
    readonly IReadOnlyList<ChatMessage> _history;
    bool _inReason;
    readonly int _seed = Random.Shared.Next();
    string _job = "";
    JsonNode? _item;
    int _outputs;

    public List<string> Texts { get; } = new();

    /// <summary>Receives progress lines while an async job runs.</summary>
    public Action<string>? OnStatus { get; set; }

    public ConnectorRun(HttpClient http, ProviderConfig cfg, string model, IReadOnlyList<ChatMessage> history,
        string? system, Func<string, byte[], Task>? onImage, CancellationToken ct)
    {
        _http = http;
        _cfg = cfg;
        _history = history;
        _model = model;
        _system = system ?? "";
        _onImage = onImage;
        _ct = ct;
        _spec = ConnectorSpec.TryParse(cfg.ConnectorJson, out var err) ?? throw new InvalidOperationException(err);

        var last = history.LastOrDefault(m => m.Role == "user");
        _prompt = last?.Content?.Trim() ?? "";
        foreach (var img in last?.Attachments.Where(a => a.Kind == "image" && !a.Excluded) ?? Enumerable.Empty<Attachment>())
            if (StorageService.ReadAttachmentSync(img) is { } bytes)
                _imgs.Add((Convert.ToBase64String(bytes), img.MimeType));
    }

    public bool IsChat => JPath.Text(_spec["kind"]) == "chat";

    // ------------------------------------------------------------ chat

    /// <summary>The conversation as OpenAI-style messages (text only, or with images as image_url parts).</summary>
    JsonArray Messages(bool withSystem, bool vision)
    {
        var arr = new JsonArray();
        if (withSystem && _system.Length > 0) arr.Add(new JsonObject { ["role"] = "system", ["content"] = _system });
        foreach (var t in HistoryHelper.Build(_history))
        {
            JsonNode content;
            if (vision && t.Images.Count > 0)
            {
                var parts = new JsonArray();
                if (t.Text.Length > 0) parts.Add(new JsonObject { ["type"] = "text", ["text"] = t.Text });
                foreach (var (mime, b64) in t.Images)
                    parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{mime};base64,{b64}" } });
                content = parts;
            }
            else content = t.Text;
            arr.Add(new JsonObject { ["role"] = t.Role, ["content"] = content });
        }
        return arr;
    }

    /// <summary>Handles one streamed JSON payload; returns the text pieces to show.</summary>
    List<string> HandlePayload(string data, JsonObject st)
    {
        var outp = new List<string>();
        JsonNode? json;
        try { json = JsonNode.Parse(data); }
        catch (JsonException) { return outp; }
        if (json == null) return outp;

        if (JPath.Text(st["errorPath"]) is { Length: > 0 } ep &&
            JPath.Select(json, ep).FirstOrDefault() is { } err && JPath.Text(err).Length > 0)
            throw new HttpRequestException(JPath.Text(err));

        string Join(string path) => path.Length == 0 ? "" :
            string.Concat(JPath.Select(json, path).Where(n => n is JsonValue).Select(JPath.Text));

        var reasoning = Join(JPath.Text(st["reasoningPath"]));
        if (reasoning.Length > 0)
        {
            if (!_inReason) { outp.Add("<think>"); _inReason = true; }
            outp.Add(reasoning);
        }

        var textPath = JPath.Text(st["textPath"]);
        if (textPath.Length == 0) textPath = JPath.Text(_spec["response"]?["text"]);
        var text = Join(textPath);
        if (text.Length > 0)
        {
            if (_inReason) { outp.Add("</think>\n\n"); _inReason = false; }
            outp.Add(text);
        }
        return outp;
    }

    public async IAsyncEnumerable<string> ChatStreamAsync()
    {
        if (_spec["request"] is not JsonObject request) throw new InvalidOperationException("Missing \"request\" in the connector.");
        var st = _spec["stream"] as JsonObject ?? new JsonObject();
        var format = JPath.Text(st["format"]);
        if (format.Length == 0) format = "sse";

        using var msg = Build(request);
        var resp = await _http.SendAsync(ProviderHttp.Customize(msg, _cfg), HttpCompletionOption.ResponseHeadersRead, _ct);
        using var _guard = new SafeDispose(resp);
        await ProviderHttp.EnsureSuccessAsync(resp, _ct);

        if (format == "none")
        {
            foreach (var piece in HandlePayload(await resp.Content.ReadAsStringAsync(_ct), st)) yield return piece;
            yield break;
        }

        if (format == "ndjson")
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(_ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = await reader.ReadLineAsync(_ct)) != null)
            {
                line = line.Trim();
                if (line.Length == 0) continue;
                foreach (var piece in HandlePayload(line, st)) yield return piece;
            }
            yield break;
        }

        await foreach (var (_, data) in ProviderHttp.ReadSseAsync(resp, _ct))
        {
            if (data == "[DONE]") yield break;
            foreach (var piece in HandlePayload(data, st)) yield return piece;
        }
    }

    // ------------------------------------------------------------ templating

    JsonNode? Resolve(string expr)
    {
        var parts = expr.Split('|');
        var name = parts[0];
        JsonNode? v = name switch
        {
            "prompt" => _prompt,
            "model" => _model,
            "key" => (_cfg.ApiKey ?? "").Trim(),
            "job" => _job,
            "system" => _system,
            "seed" => _seed,
            "temperature" => _cfg.Temperature,
            "top_p" => _cfg.TopP,
            "max_tokens" => _cfg.MaxTokens,
            "messages" => Messages(false, false),
            "messages_with_system" => Messages(true, false),
            "messages_vision" => Messages(false, true),
            "messages_vision_with_system" => Messages(true, true),
            "images_base64" => _imgs.Count == 0 ? null : new JsonArray(_imgs.Select(i => (JsonNode?)i.B64).ToArray()),
            "item" => _item?.DeepClone(),
            _ => null
        };
        if (v == null && ImgRef.Match(name) is { Success: true } im)
        {
            var idx = im.Groups[1].Value.Length == 0 ? 1 : int.Parse(im.Groups[1].Value);
            if (idx >= 1 && idx <= _imgs.Count)
            {
                var (b64, mime) = _imgs[idx - 1];
                v = im.Groups[2].Value switch { "base64" => b64, "mime" => mime, _ => $"data:{mime};base64,{b64}" };
            }
        }
        if (v == null && name.StartsWith("input.")) v = InputValue(name[6..]);
        if (v == null && name.StartsWith("item.") && _item != null)
            v = JPath.Select(_item, "$." + name[5..]).FirstOrDefault()?.DeepClone();

        if (v != null && parts.Skip(1).Any(f => f.Trim() == "url"))
            v = Uri.EscapeDataString(JPath.Text(v));
        return v;
    }

    JsonNode? InputValue(string id) =>
        ConnectorSpec.InputValue(ConnectorSpec.Inputs(_cfg.ConnectorJson), _cfg.ConnectorInputs, id);

    JsonNode? Expand(JsonNode? node) => Tpl.Expand(node, Resolve);

    string ExpandText(string? s) => JPath.Text(Expand(JsonValue.Create(s ?? "")));

    // ------------------------------------------------------------ requests

    HttpRequestMessage Build(JsonObject req)
    {
        var method = new HttpMethod((JPath.Text(req["method"]) is { Length: > 0 } m ? m : "POST").ToUpperInvariant());
        var path = ExpandText(JPath.Text(req["path"]));
        var url = path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? path : ProviderHttp.Url(_cfg, path);

        if (req["query"] is JsonObject q)
        {
            var sb = new StringBuilder(url);
            var sep = url.Contains('?') ? '&' : '?';
            foreach (var (k, v) in q)
            {
                var e = Expand(v);
                if (e == null) continue;
                sb.Append(sep).Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(JPath.Text(e)));
                sep = '&';
            }
            url = sb.ToString();
        }

        var msg = new HttpRequestMessage(method, url);

        var bodyType = JPath.Text(req["bodyType"]);
        if (req["body"] != null && method != HttpMethod.Get && bodyType != "none")
        {
            var body = Expand(req["body"]);
            if (bodyType == "form" && body is JsonObject fo)
                msg.Content = new FormUrlEncodedContent(fo.Where(kv => kv.Value != null)
                    .Select(kv => new KeyValuePair<string, string>(kv.Key, JPath.Text(kv.Value))));
            else if (bodyType == "multipart" && body is JsonObject mo)
            {
                var mp = new MultipartFormDataContent();
                foreach (var (k, v) in mo)
                {
                    if (v == null) continue;
                    if (v is JsonObject fileObj)
                    {
                        if (fileObj["file"] == null) continue; // file not available (e.g. no image attached)
                        var raw = JPath.Text(fileObj["file"]);
                        if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) raw = raw[(raw.IndexOf(',') + 1)..];
                        var data = Convert.FromBase64String(raw);
                        var mime = JPath.Text(fileObj["mime"]) is { Length: > 0 } fm ? fm : Normalize("", data);
                        var part = new ByteArrayContent(data);
                        part.Headers.ContentType = MediaTypeHeaderValue.Parse(mime);
                        var fname = JPath.Text(fileObj["filename"]) is { Length: > 0 } fn ? fn : "file" + FileExportService.ExtensionFromMime(mime);
                        mp.Add(part, k, fname);
                    }
                    else mp.Add(new StringContent(JPath.Text(v)), k);
                }
                msg.Content = mp;
            }
            else if (bodyType == "text")
                msg.Content = new StringContent(JPath.Text(body), Encoding.UTF8, "text/plain");
            else if (body != null)
                msg.Content = ProviderHttp.JsonBody(body);
        }

        ApplyHeaders(msg, req["headers"]);
        ApplyAuth(msg);
        return msg;
    }

    void ApplyHeaders(HttpRequestMessage msg, JsonNode? extra)
    {
        foreach (var h in new[] { _spec["headers"], extra })
        {
            if (h is not JsonObject ho) continue;
            foreach (var (k, v) in ho)
            {
                var val = JPath.Text(Expand(v));
                if (val.Length == 0) continue;
                msg.Headers.Remove(k);
                if (!msg.Headers.TryAddWithoutValidation(k, val)) msg.Content?.Headers.TryAddWithoutValidation(k, val);
            }
        }
    }

    void ApplyAuth(HttpRequestMessage msg)
    {
        var key = (_cfg.ApiKey ?? "").Trim();
        if (key.Length == 0 || _cfg.AuthMode != "default" || _spec["auth"] is not JsonObject a) return;
        var type = JPath.Text(a["type"]);
        var name = JPath.Text(a["name"]);
        switch (type)
        {
            case "bearer":
                msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                break;
            case "header" when name.Length > 0:
                msg.Headers.Remove(name);
                msg.Headers.TryAddWithoutValidation(name, key);
                break;
            case "query" when name.Length > 0 && msg.RequestUri != null:
                var u = msg.RequestUri.ToString();
                msg.RequestUri = new Uri(u + (u.Contains('?') ? "&" : "?") + Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(key));
                break;
        }
    }

    async Task<(byte[] Bytes, string Type)> SendAsync(JsonObject req)
    {
        using var msg = Build(req);
        var resp = await _http.SendAsync(ProviderHttp.Customize(msg, _cfg), HttpCompletionOption.ResponseContentRead, _ct);
        using var _guard = new SafeDispose(resp);
        await ProviderHttp.EnsureSuccessAsync(resp, _ct);
        return (await resp.Content.ReadAsByteArrayAsync(_ct), resp.Content.Headers.ContentType?.MediaType ?? "");
    }

    /// <summary>Sends a request spec and parses the answer as JSON (used for model lists).</summary>
    public async Task<JsonNode?> FetchJsonAsync(JsonObject req)
    {
        var (bytes, _) = await SendAsync(req);
        return JsonNode.Parse(bytes);
    }

    // ------------------------------------------------------------ flow

    public async Task ExecuteAsync()
    {
        if (_spec["request"] is not JsonObject request) throw new InvalidOperationException("Missing \"request\" in the connector.");
        if (IsChat) throw new InvalidOperationException("Chat connectors are streamed.");
        if (string.IsNullOrWhiteSpace(_prompt) && _spec["allowEmptyPrompt"]?.GetValue<bool>() != true &&
            JsonUsesPrompt(_spec))
            throw new InvalidOperationException("Write a message first (it is used as the prompt).");

        var (bytes, type) = await SendAsync(request);

        if (_spec["job"] is JsonObject job)
        {
            var submitted = ParseJson(bytes);
            var id = JPath.Select(submitted, JPath.Text(job["id"])).FirstOrDefault();
            if (id == null)
                throw new InvalidOperationException("The job id was not found in the response: " + Snippet(bytes));
            _job = JPath.Text(id);
            await PollAsync(job);
            return;
        }

        await ExtractAsync(_spec["response"] as JsonObject, bytes, type);
        Finish(bytes);
    }

    static bool JsonUsesPrompt(JsonObject spec) => spec.ToJsonString().Contains("{{prompt");

    async Task PollAsync(JsonObject job)
    {
        var poll = (JsonObject)job["poll"]!;
        var interval = TimeSpan.FromSeconds(Math.Max(1, job["poll"]?["intervalSec"]?.GetValue<double>() ?? 3));
        var timeout = TimeSpan.FromSeconds(Math.Max(5, job["poll"]?["timeoutSec"]?.GetValue<double>() ?? 600));
        var resp = job["response"] as JsonObject ?? _spec["response"] as JsonObject;
        var statusPath = JPath.Text(poll["statusPath"]);
        var done = ListOf(poll["done"]);
        var failed = ListOf(poll["failed"]);
        var started = DateTime.UtcNow;

        var progressPath = JPath.Text(poll["progressPath"]);
        var progressMax = poll["progressMax"]?.GetValue<double>() ?? 100;

        try
        {
            while (true)
            {
                await Task.Delay(interval, _ct);
                var elapsed = DateTime.UtcNow - started;
                if (elapsed > timeout)
                    throw new TimeoutException("The job did not finish in time (" + (int)timeout.TotalSeconds + " s).");

                var (bytes, type) = await SendAsync(poll);
                var json = type.Contains("json", StringComparison.OrdinalIgnoreCase) || LooksJson(bytes) ? ParseJson(bytes) : null;

                var status = statusPath.Length > 0 && json != null
                    ? JPath.Text(JPath.Select(json, statusPath).FirstOrDefault()) : "";
                if (statusPath.Length > 0 && json != null)
                {
                    var st = status.ToLowerInvariant();
                    if (failed.Contains(st))
                    {
                        var ep = JPath.Text(poll["errorPath"]);
                        var em = ep.Length > 0 ? JPath.Text(JPath.Select(json, ep).FirstOrDefault()) : "";
                        throw new InvalidOperationException("The job failed" + (em.Length > 0 ? ": " + em : ": " + Snippet(bytes)));
                    }
                    if (!done.Contains(st)) { ReportProgress(status, progressPath, progressMax, json, elapsed); continue; }
                }
                else if (!HasResults(resp, json))
                {
                    ReportProgress(status, progressPath, progressMax, json, elapsed);
                    continue;
                }

                await ExtractAsync(resp, bytes, type);
                Finish(bytes);
                return;
            }
        }
        catch (OperationCanceledException) when (_ct.IsCancellationRequested)
        {
            await CancelJobAsync(job); // the user pressed Stop: ask the server to stop too
            throw;
        }
    }

    void ReportProgress(string status, string progressPath, double max, JsonNode? json, TimeSpan elapsed)
    {
        if (OnStatus == null) return;
        var parts = new List<string>();
        if (status.Length > 0) parts.Add(status);
        if (progressPath.Length > 0 && json != null &&
            double.TryParse(JPath.Text(JPath.Select(json, progressPath).FirstOrDefault()),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && max > 0)
            parts.Add(Math.Clamp(v / max * 100, 0, 100).ToString("0") + "%");
        parts.Add($"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}");
        OnStatus(string.Join(" · ", parts));
    }

    /// <summary>Sends the optional "job.cancel" request (best effort, never throws).</summary>
    async Task CancelJobAsync(JsonObject job)
    {
        if (job["cancel"] is not JsonObject cancel || _job.Length == 0) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var msg = Build(cancel);
            using var resp = await _http.SendAsync(ProviderHttp.Customize(msg, _cfg), cts.Token);
        }
        catch { /* the job may already be gone */ }
    }

    static List<string> ListOf(JsonNode? n) =>
        n is JsonArray a ? a.Select(x => JPath.Text(x).ToLowerInvariant()).ToList()
        : n != null ? new List<string> { JPath.Text(n).ToLowerInvariant() } : new List<string>();

    static bool HasResults(JsonObject? resp, JsonNode? json)
    {
        if (json == null || resp?["results"] is not JsonArray rules) return false;
        return rules.OfType<JsonObject>().Any(r => JPath.Select(json, JPath.Text(r["from"])).Count > 0);
    }

    void Finish(byte[] lastBytes)
    {
        if (_outputs == 0 && Texts.Count == 0)
            throw new InvalidOperationException("The connector returned no result: " + Snippet(lastBytes));
    }

    // ------------------------------------------------------------ results

    async Task ExtractAsync(JsonObject? resp, byte[] bytes, string contentType)
    {
        var kind = JPath.Text(resp?["type"]);
        var isJson = contentType.Contains("json", StringComparison.OrdinalIgnoreCase) || LooksJson(bytes);

        if (kind == "binary" || (kind.Length == 0 && !isJson && bytes.Length > 0 && !contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)))
        {
            var mime = JPath.Text(resp?["mime"]) is { Length: > 0 } fm ? fm : Normalize(contentType, bytes);
            await EmitAsync(mime, bytes);
            return;
        }

        var json = ParseJson(bytes);
        if (resp == null) { Texts.Add(JPath.Text(json)); return; }

        if (JPath.Text(resp["text"]) is { Length: > 0 } tp)
            foreach (var t in JPath.Select(json, tp)) Texts.Add(JPath.Text(t));

        if (resp["results"] is not JsonArray rules) return;
        foreach (var rule in rules.OfType<JsonObject>())
        {
            var from = JPath.Text(rule["from"]);
            var how = JPath.Text(rule["as"]);
            var ruleMime = JPath.Text(rule["mime"]);
            foreach (var node in JPath.Select(json, from.Length > 0 ? from : "$"))
            {
                switch (how)
                {
                    case "text":
                        Texts.Add(JPath.Text(node));
                        break;
                    case "base64":
                        await EmitBase64Async(JPath.Text(node), ruleMime);
                        break;
                    default: // "url"
                        _item = node;
                        var url = JPath.Text(rule["urlTemplate"]) is { Length: > 0 } tpl ? ExpandText(tpl) : JPath.Text(node);
                        _item = null;
                        await DownloadAsync(url, ruleMime);
                        break;
                }
            }
        }
    }

    async Task EmitBase64Async(string data, string ruleMime)
    {
        var mime = ruleMime;
        if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = data.IndexOf(',');
            var semi = data.IndexOf(';');
            if (mime.Length == 0 && comma > 5) mime = data[5..(semi > 5 && semi < comma ? semi : comma)];
            data = data[(comma + 1)..];
        }
        var bytes = Convert.FromBase64String(data.Trim());
        await EmitAsync(mime.Length > 0 ? mime : Normalize("", bytes), bytes);
    }

    async Task DownloadAsync(string url, string ruleMime)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) { await EmitBase64Async(url, ruleMime); return; }

        var relative = !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var full = relative ? ProviderHttp.Url(_cfg, url) : url;
        using var msg = new HttpRequestMessage(HttpMethod.Get, full);

        // send the key only to the API's own host
        var sameHost = relative || (Uri.TryCreate(full, UriKind.Absolute, out var u) &&
            Uri.TryCreate(ProviderHttp.Url(_cfg, ""), UriKind.Absolute, out var b) &&
            string.Equals(u.Host, b.Host, StringComparison.OrdinalIgnoreCase));
        if (sameHost) { ApplyHeaders(msg, null); ApplyAuth(msg); ProviderHttp.Customize(msg, _cfg); }

        var resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseContentRead, _ct);
        using var _guard = new SafeDispose(resp);
        await ProviderHttp.EnsureSuccessAsync(resp, _ct);
        var bytes = await resp.Content.ReadAsByteArrayAsync(_ct);
        var ct = resp.Content.Headers.ContentType?.MediaType ?? "";
        var mime = ruleMime.Length > 0 ? ruleMime : Normalize(ct, bytes, full);
        await EmitAsync(mime, bytes);
    }

    async Task EmitAsync(string mime, byte[] bytes)
    {
        if (_onImage == null) throw new InvalidOperationException("Results cannot be shown here.");
        await _onImage(mime, bytes);
        _outputs++;
    }

    // ------------------------------------------------------------ helpers

    static JsonNode? ParseJson(byte[] bytes)
    {
        try { return JsonNode.Parse(bytes); }
        catch (JsonException) { throw new InvalidOperationException("The server did not return JSON: " + Snippet(bytes)); }
    }

    static bool LooksJson(byte[] b)
    {
        foreach (var x in b.Take(16))
        {
            if (x is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t') continue;
            return x == (byte)'{' || x == (byte)'[';
        }
        return false;
    }

    static string Snippet(byte[] b) => Encoding.UTF8.GetString(b, 0, Math.Min(b.Length, 300));

    /// <summary>Uses the server's content type; when it is generic, guesses from the URL or the first bytes.</summary>
    internal static string Normalize(string contentType, byte[] b, string? url = null)
    {
        var ct = (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        if (ct.Length > 0 && ct != "application/octet-stream" && ct != "binary/octet-stream") return ct;

        var ext = url == null ? "" : Path.GetExtension(new Uri(url, UriKind.RelativeOrAbsolute).IsAbsoluteUri ? new Uri(url).AbsolutePath : url.Split('?')[0]).ToLowerInvariant();
        var byExt = ext switch
        {
            ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif",
            ".mp4" => "video/mp4", ".webm" => "video/webm", ".mov" => "video/quicktime",
            ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".ogg" => "audio/ogg", ".flac" => "audio/flac",
            ".glb" => "model/gltf-binary", ".gltf" => "model/gltf+json", ".obj" => "model/obj", ".zip" => "application/zip",
            _ => ""
        };
        if (byExt.Length > 0) return byExt;

        bool Starts(params byte[] p) => b.Length >= p.Length && b.Take(p.Length).SequenceEqual(p);
        if (Starts(0x89, 0x50, 0x4E, 0x47)) return "image/png";
        if (Starts(0xFF, 0xD8)) return "image/jpeg";
        if (Starts(0x47, 0x49, 0x46)) return "image/gif";
        if (Starts(0x1A, 0x45, 0xDF, 0xA3)) return "video/webm";
        if (Starts(0x67, 0x6C, 0x54, 0x46)) return "model/gltf-binary";
        if (Starts(0x49, 0x44, 0x33) || Starts(0xFF, 0xFB)) return "audio/mpeg";
        if (Starts(0x4F, 0x67, 0x67, 0x53)) return "audio/ogg";
        if (Starts(0x50, 0x4B)) return "application/zip";
        if (b.Length > 12 && Starts(0x52, 0x49, 0x46, 0x46))
        {
            var tag = Encoding.ASCII.GetString(b, 8, 4);
            if (tag == "WEBP") return "image/webp";
            if (tag == "WAVE") return "audio/wav";
        }
        if (b.Length > 12 && Encoding.ASCII.GetString(b, 4, 4) == "ftyp") return "video/mp4";
        return "application/octet-stream";
    }
}
