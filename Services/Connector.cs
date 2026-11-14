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

/// <summary>One UI field declared by a connector ("inputs").</summary>
public record ConnInput(string Id, string Label, string Type, List<string> Options, string Default);

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

    public static List<ConnInput> Inputs(string? json)
    {
        var list = new List<ConnInput>();
        var root = TryParse(json, out _);
        if (root?["inputs"] is not JsonArray arr) return list;
        foreach (var it in arr)
        {
            if (it is not JsonObject o || JPath.Text(o["id"]) is not { Length: > 0 } id) continue;
            var type = JPath.Text(o["type"]);
            if (type.Length == 0) type = "text";
            var opts = o["options"] is JsonArray oa ? oa.Select(JPath.Text).ToList() : new List<string>();
            list.Add(new ConnInput(id, JPath.Text(o["label"]) is { Length: > 0 } l ? l : id, type, opts, JPath.Text(o["default"])));
        }
        return list;
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
                CheckResponse(job["response"]);
                if (job["response"] == null && root["response"] == null)
                    return "Define \"response\" (or job.response) to say where the result is.";
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

public class ConnectorProvider : IChatProvider
{
    readonly HttpClient _http;
    public ConnectorProvider(HttpClient http) => _http = http;

    public async IAsyncEnumerable<string> StreamAsync(ProviderConfig cfg, string model,
        IReadOnlyList<ChatMessage> history, string? systemPrompt, Func<string, byte[], Task>? onImage,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var run = new ConnectorRun(_http, cfg, model, history, systemPrompt, onImage, ct);
        await run.ExecuteAsync();
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
    static readonly Regex Whole = new(@"^\s*\{\{\s*([A-Za-z0-9_.\-|]+)\s*\}\}\s*$", RegexOptions.Compiled);
    static readonly Regex Inline = new(@"\{\{\s*([A-Za-z0-9_.\-|]+)\s*\}\}", RegexOptions.Compiled);

    readonly HttpClient _http;
    readonly ProviderConfig _cfg;
    readonly JsonObject _spec;
    readonly Func<string, byte[], Task>? _onImage;
    readonly CancellationToken _ct;

    readonly string _prompt, _model, _system;
    readonly string? _imgB64, _imgMime;
    readonly int _seed = Random.Shared.Next();
    string _job = "";
    JsonNode? _item;
    int _outputs;

    public List<string> Texts { get; } = new();

    public ConnectorRun(HttpClient http, ProviderConfig cfg, string model, IReadOnlyList<ChatMessage> history,
        string? system, Func<string, byte[], Task>? onImage, CancellationToken ct)
    {
        _http = http;
        _cfg = cfg;
        _model = model;
        _system = system ?? "";
        _onImage = onImage;
        _ct = ct;
        _spec = ConnectorSpec.TryParse(cfg.ConnectorJson, out var err) ?? throw new InvalidOperationException(err);

        var last = history.LastOrDefault(m => m.Role == "user");
        _prompt = last?.Content?.Trim() ?? "";
        var img = last?.Attachments.FirstOrDefault(a => a.Kind == "image" && !a.Excluded);
        if (img != null && StorageService.ReadAttachmentSync(img) is { } bytes)
        {
            _imgB64 = Convert.ToBase64String(bytes);
            _imgMime = img.MimeType;
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
            "image_base64" => _imgB64,
            "image_mime" => _imgMime,
            "image_dataurl" => _imgB64 == null ? null : $"data:{_imgMime};base64,{_imgB64}",
            "item" => _item?.DeepClone(),
            _ => null
        };
        if (v == null && name.StartsWith("input.")) v = InputValue(name[6..]);
        if (v == null && name.StartsWith("item.") && _item != null)
            v = JPath.Select(_item, "$." + name[5..]).FirstOrDefault()?.DeepClone();

        if (v != null && parts.Skip(1).Any(f => f.Trim() == "url"))
            v = Uri.EscapeDataString(JPath.Text(v));
        return v;
    }

    JsonNode? InputValue(string id)
    {
        var def = ConnectorSpec.Inputs(_cfg.ConnectorJson).FirstOrDefault(i => i.Id == id);
        string? raw = _cfg.ConnectorInputs.TryGetValue(id, out var set) ? set : def?.Default;
        if (string.IsNullOrEmpty(raw)) return null;
        switch (def?.Type)
        {
            case "number":
                if (long.TryParse(raw, out var l)) return l;
                if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
                return null;
            case "bool":
                return raw.Equals("true", StringComparison.OrdinalIgnoreCase);
            default:
                return raw;
        }
    }

    JsonNode? Expand(JsonNode? node)
    {
        switch (node)
        {
            case null: return null;
            case JsonObject o:
            {
                var r = new JsonObject();
                foreach (var (k, v) in o)
                {
                    var e = Expand(v);
                    if (e == null && (v == null ? false : IsPlaceholder(v))) continue; // unset placeholder: drop the field
                    r[k] = e;
                }
                return r;
            }
            case JsonArray a:
            {
                var r = new JsonArray();
                foreach (var v in a)
                {
                    var e = Expand(v);
                    if (e == null && v != null && IsPlaceholder(v)) continue;
                    r.Add(e);
                }
                return r;
            }
            case JsonValue v when v.TryGetValue<string>(out var s):
            {
                var m = Whole.Match(s);
                if (m.Success) return Resolve(m.Groups[1].Value);
                return Inline.Replace(s, mm => JPath.Text(Resolve(mm.Groups[1].Value)));
            }
            default:
                return node.DeepClone();
        }
    }

    static bool IsPlaceholder(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) && Whole.IsMatch(s);

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

        while (true)
        {
            await Task.Delay(interval, _ct);
            if (DateTime.UtcNow - started > timeout)
                throw new TimeoutException("The job did not finish in time (" + (int)timeout.TotalSeconds + " s).");

            var (bytes, type) = await SendAsync(poll);
            var json = type.Contains("json", StringComparison.OrdinalIgnoreCase) || LooksJson(bytes) ? ParseJson(bytes) : null;

            if (statusPath.Length > 0 && json != null)
            {
                var st = JPath.Text(JPath.Select(json, statusPath).FirstOrDefault()).ToLowerInvariant();
                if (failed.Contains(st))
                {
                    var ep = JPath.Text(poll["errorPath"]);
                    var em = ep.Length > 0 ? JPath.Text(JPath.Select(json, ep).FirstOrDefault()) : "";
                    throw new InvalidOperationException("The job failed" + (em.Length > 0 ? ": " + em : ": " + Snippet(bytes)));
                }
                if (!done.Contains(st)) continue;
            }
            else if (!HasResults(resp, json))
            {
                continue;
            }

            await ExtractAsync(resp, bytes, type);
            Finish(bytes);
            return;
        }
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
