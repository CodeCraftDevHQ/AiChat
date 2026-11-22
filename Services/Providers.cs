using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiChat.Models;

namespace AiChat.Services;

public interface IChatProvider
{
    IAsyncEnumerable<string> StreamAsync(ProviderConfig cfg, string model, IReadOnlyList<ChatMessage> history,
        string? systemPrompt, Func<string, byte[], Task>? onImage, CancellationToken ct);

    Task<List<string>> ListModelsAsync(ProviderConfig cfg, CancellationToken ct);
}

public class ProviderFactory
{
    readonly OpenAiCompatibleProvider _openAi;
    readonly AnthropicProvider _anthropic;
    readonly GeminiProvider _gemini;
    readonly FooocusApiProvider _fooocus;
    readonly ConnectorProvider _connector;

    public ProviderFactory(HttpClient http)
    {
        _fooocus = new FooocusApiProvider(http);
        _connector = new ConnectorProvider(http);
        _openAi = new OpenAiCompatibleProvider(http);
        _anthropic = new AnthropicProvider(http);
        _gemini = new GeminiProvider(http);
    }

    public IChatProvider Create(ProviderType type) => type switch
    {
        ProviderType.Anthropic => _anthropic,
        ProviderType.Gemini => _gemini,
        ProviderType.FooocusApi => _fooocus,
        ProviderType.Custom => _connector,
        _ => _openAi
    };
}

// ---------------------------------------------------------------- shared helpers

static class HistoryHelper
{
    /// <summary>One request message: text plus (base64) images.</summary>
    public class Turn
    {
        public string Role = "user";
        public string Text = "";
        public List<(string Mime, string Base64)> Images = new();
        public List<string> Pdfs = new(); // base64, original files (providers with native PDF support only)
    }

    /// <summary>
    /// Turns the stored messages into a clean list:
    /// text attachments are inlined into the text, image attachments become base64 images,
    /// empty/failed replies are dropped, consecutive messages from the same role are merged,
    /// and the list always starts with a user message (Claude and Gemini require that).
    /// </summary>
    public static List<Turn> Build(IReadOnlyList<ChatMessage> history, bool nativePdf = false)
    {
        var result = new List<Turn>();
        foreach (var m in history)
        {
            // the model's own "thinking" is shown to the user but not sent back
            var text = m.Role == "assistant" ? ToolMarks.Strip(MarkdownRenderer.StripThink(m.Content ?? "")) : m.Content ?? "";
            var images = new List<(string, string)>();
            var pdfs = new List<string>();

            foreach (var a in m.Attachments)
            {
                if (a.Excluded)
                {
                    text += $"\n\n[Attachment \"{a.Name}\" was not sent: the model does not support this file type]";
                    continue;
                }
                var bytes = StorageService.ReadAttachmentSync(a);
                if (bytes == null)
                {
                    text += $"\n\n[File \"{a.Name}\" is no longer available]";
                }
                else if (a.Kind == "pdf" && a.PdfMode == "file" && nativePdf)
                {
                    pdfs.Add(Convert.ToBase64String(bytes));
                    text += $"\n\n[Attached PDF: {a.Name}]";
                }
                else if (a.Kind == "pdf")
                {
                    text += $"\n\n--- PDF: {a.Name} ---\n{PdfHelper.ExtractText(bytes, a.Id)}\n--- End of PDF ---";
                }
                else if (a.Kind is "video" or "audio" or "file")
                {
                    text += m.Role == "user"
                        ? $"\n\n[Attached {a.Kind} \"{a.Name}\": this model cannot read it; it is only used by API connectors]"
                        : $"\n\n[Generated {a.Kind}: {a.Name}]";
                }
                else if (a.Kind == "image")
                {
                    images.Add((a.MimeType, Convert.ToBase64String(bytes)));
                }
                else
                {
                    text += $"\n\n--- File: {a.Name} ---\n{Encoding.UTF8.GetString(bytes)}\n--- End of file ---";
                }
            }

            text = text.Trim();
            if (text.Length == 0 && images.Count == 0 && pdfs.Count == 0) continue;

            if (result.Count > 0 && result[^1].Role == m.Role)
            {
                var last = result[^1];
                last.Text = last.Text.Length == 0 ? text : text.Length == 0 ? last.Text : last.Text + "\n\n" + text;
                last.Images.AddRange(images);
                last.Pdfs.AddRange(pdfs);
            }
            else
            {
                var t = new Turn { Role = m.Role, Text = text };
                t.Images.AddRange(images);
                t.Pdfs.AddRange(pdfs);
                result.Add(t);
            }
        }
        while (result.Count > 0 && result[0].Role != "user") result.RemoveAt(0);
        return result;
    }
}

static class ImageHelper
{
    /// <summary>
    /// A data: URI is stored as a file through onImage (returns null);
    /// anything else (http URL) is returned as a Markdown image so the chat can show it.
    /// </summary>
    public static async Task<string?> HandleUrlAsync(string url, Func<string, byte[], Task>? onImage)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = url.IndexOf(',');
            var semi = url.IndexOf(';');
            if (comma > 5 && onImage != null)
            {
                var mime = url[5..(semi > 5 && semi < comma ? semi : comma)];
                try
                {
                    await onImage(string.IsNullOrWhiteSpace(mime) ? "image/png" : mime,
                        Convert.FromBase64String(url[(comma + 1)..]));
                    return null;
                }
                catch (FormatException)
                {
                    return null;
                }
            }
            return null;
        }

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return $"\n\n![image]({url})\n\n";

        return null;
    }
}

/// <summary>Disposes without ever throwing (native Android streams can throw on Dispose).</summary>
readonly struct SafeDispose : IDisposable
{
    readonly IDisposable? _d;
    public SafeDispose(IDisposable? d) => _d = d;
    public void Dispose() { try { _d?.Dispose(); } catch { } }
}

static class ProviderHttp
{
    public static string Url(ProviderConfig cfg, string path)
    {
        var baseUrl = string.IsNullOrWhiteSpace(cfg.BaseUrl) ? ProviderDefaults.BaseUrl(cfg.Type) : cfg.BaseUrl.Trim();
        return baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
    }

    /// <summary>Chat path: the user's override, else the default of the API type.</summary>
    public static string ChatPath(ProviderConfig cfg, string defaultPath, string? model = null) =>
        string.IsNullOrWhiteSpace(cfg.ChatPath) ? defaultPath : cfg.ChatPath.Trim().Replace("{model}", model ?? "");

    public static string ModelsPath(ProviderConfig cfg, string defaultPath) =>
        string.IsNullOrWhiteSpace(cfg.ModelsPath) ? defaultPath : cfg.ModelsPath.Trim();

    /// <summary>Adds temperature / top_p / max_tokens when they are set (before the extra body, so that can still override).</summary>
    public static void ApplyGen(JsonObject body, ProviderConfig cfg, string maxTokensField = "max_tokens")
    {
        if (cfg.Temperature != null) body["temperature"] = cfg.Temperature;
        if (cfg.TopP != null) body["top_p"] = cfg.TopP;
        if (cfg.MaxTokens != null) body[maxTokensField] = cfg.MaxTokens;
    }

    /// <summary>Merges the user's "extra body" JSON into the request body. A null value removes the field.</summary>
    public static JsonObject MergeBody(JsonObject body, ProviderConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.ExtraBody)) return body;
        var defs = ConnectorSpec.ControlInputs(cfg.Controls);
        var expanded = Tpl.Expand(JsonNode.Parse(cfg.ExtraBody),
            name => name.StartsWith("input.") ? ConnectorSpec.InputValue(defs, cfg.ConnectorInputs, name[6..]) : null);
        if (expanded is not JsonObject extra)
            throw new InvalidOperationException("The extra body must be a JSON object.");
        foreach (var (k, v) in extra.ToList())
        {
            if (v == null) body.Remove(k);
            else body[k] = v.DeepClone();
        }
        return body;
    }

    /// <summary>Parses "Name: value" lines; {apiKey} in a value is replaced by the key.</summary>
    public static List<(string Name, string Value)> ParseHeaders(string text, string apiKey)
    {
        var list = new List<(string, string)>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var i = line.IndexOf(':');
            if (i <= 0) throw new FormatException($"Header line without a colon: {line}");
            list.Add((line[..i].Trim(), line[(i + 1)..].Trim().Replace("{apiKey}", apiKey)));
        }
        return list;
    }

    static void SetHeader(HttpRequestMessage req, string name, string value)
    {
        req.Headers.Remove(name);
        if (!req.Headers.TryAddWithoutValidation(name, value))
            req.Content?.Headers.TryAddWithoutValidation(name, value);
    }

    /// <summary>Applies the user's advanced settings (auth style, extra headers) to a request just before it is sent.</summary>
    public static HttpRequestMessage Customize(HttpRequestMessage req, ProviderConfig cfg)
    {
        var key = (cfg.ApiKey ?? "").Trim();
        var mode = string.IsNullOrWhiteSpace(cfg.AuthMode) ? "default" : cfg.AuthMode;

        if (mode != "default")
        {
            req.Headers.Authorization = null;
            req.Headers.Remove("x-api-key");
            req.Headers.Remove("x-goog-api-key");
            switch (mode)
            {
                case "bearer":
                    if (key.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                    break;
                case "header":
                    if (key.Length > 0 && !string.IsNullOrWhiteSpace(cfg.AuthName)) SetHeader(req, cfg.AuthName.Trim(), key);
                    break;
                case "query":
                    if (key.Length > 0 && !string.IsNullOrWhiteSpace(cfg.AuthName) && req.RequestUri != null)
                    {
                        var u = req.RequestUri.ToString();
                        req.RequestUri = new Uri(u + (u.Contains('?') ? "&" : "?") + Uri.EscapeDataString(cfg.AuthName.Trim()) + "=" + Uri.EscapeDataString(key));
                    }
                    break;
            }
        }

        if (!string.IsNullOrWhiteSpace(cfg.Headers))
            foreach (var (n, v) in ParseHeaders(cfg.Headers, key)) SetHeader(req, n, v);

        return req;
    }

    public static StringContent JsonBody(JsonNode node) =>
        new(node.ToJsonString(), Encoding.UTF8, "application/json");

    public static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        var ex = new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {ExtractError(body)}", null, resp.StatusCode);
        var wait = HeaderWaitSeconds(resp);
        if (wait != null) ex.Data["RetryAfter"] = wait.Value;
        throw ex;
    }

    /// <summary>Wait hint from the response headers (Retry-After, retry-after-ms, x-ratelimit-reset*).</summary>
    static int? HeaderWaitSeconds(HttpResponseMessage resp)
    {
        var ra = resp.Headers.RetryAfter;
        if (ra?.Delta != null) return (int)Math.Ceiling(ra.Delta.Value.TotalSeconds);
        if (ra?.Date != null) return (int)Math.Max(1, Math.Ceiling((ra.Date.Value - DateTimeOffset.UtcNow).TotalSeconds));

        foreach (var (name, ms) in new[] { ("retry-after-ms", true), ("x-ratelimit-reset-requests", false), ("x-ratelimit-reset", false), ("ratelimit-reset", false) })
        {
            if (!resp.Headers.TryGetValues(name, out var vals)) continue;
            var v = vals.FirstOrDefault()?.Trim();
            if (string.IsNullOrEmpty(v)) continue;
            if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n))
            {
                if (ms) return (int)Math.Ceiling(n / 1000);
                if (n > 1e12) return (int)Math.Max(1, Math.Ceiling((n / 1000) - DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                if (n > 1e9) return (int)Math.Max(1, Math.Ceiling(n - DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                return (int)Math.Ceiling(n);
            }
            var go = ParseGoDuration(v);
            if (go != null) return go;
        }
        return null;
    }

    // "1s", "6m0s", "20ms", "1h2m3s"
    static int? ParseGoDuration(string v)
    {
        var m = System.Text.RegularExpressions.Regex.Matches(v, @"(\d+(?:\.\d+)?)(ms|h|m|s)");
        if (m.Count == 0) return null;
        double total = 0;
        foreach (System.Text.RegularExpressions.Match x in m)
        {
            var n = double.Parse(x.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            total += x.Groups[2].Value switch { "ms" => n / 1000, "s" => n, "m" => n * 60, _ => n * 3600 };
        }
        return (int)Math.Ceiling(total);
    }

    public record RateDetection(bool Limited, int? RetrySeconds, bool Daily, string Text);

    static readonly string[] LimitWords =
    {
        "rate limit", "rate-limit", "ratelimit", "rate_limit", "rate limited", "rate-limited", "too many requests", "too_many_requests",
        "resource_exhausted", "resource exhausted", "quota", "requests per", "per minute", "per min", "perminute", "rpm", "tpm",
        "try again in", "retry in", "retry after", "retry-after", "slow down", "throttl", "overloaded", "at capacity",
        "free-models-per", "usage limit", "limit reached"
    };

    static readonly string[] DailyWords =
    {
        "per day", "perday", "per_day", "per-day", "daily", "free-models-per-day", "insufficient_quota", "billing details",
        "exceeded your current quota", "resets at midnight"
    };

    /// <summary>
    /// Is this error a "too many requests" one? Looks at the HTTP status, the Retry-After style headers, wait hints inside
    /// the message (retryDelay, "try again in 20s", X-RateLimit-Reset) and well-known wording, plus the user's own keywords.
    /// </summary>
    /// <summary>The API refused the request because of the "tools" field (model without tool support).</summary>
    public static bool LooksLikeToolRejection(string message)
    {
        var t = (message ?? "").ToLowerInvariant();
        if (t.Contains("429") || t.Contains("rate limit") || t.Contains("401") || t.Contains("403")) return false;
        return t.Contains("tool") || t.Contains("function call") || t.Contains("function_call");
    }

    public static RateDetection RateInfo(Exception ex, IReadOnlyList<string>? extraWords = null)
    {
        var limited = false;
        int? retry = null;
        var text = new StringBuilder();
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests }) limited = true;
            if (e.Data["RetryAfter"] is int s) retry = s;
            text.Append(e.Message).Append(' ');
        }
        var raw = text.ToString();
        var t = raw.ToLowerInvariant();

        // "HTTP 429" / "429 Too Many" / "code":429
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"(^|[^\d])429([^\d]|$)")) limited = true;
        if (LimitWords.Any(t.Contains)) limited = true;
        if (extraWords != null && extraWords.Any(w => w.Length > 0 && t.Contains(w.ToLowerInvariant()))) limited = true;

        if (limited && retry == null) retry = WaitFromText(raw);

        var daily = limited && DailyWords.Any(t.Contains) &&
                    !(t.Contains("per minute") || t.Contains("perminute") || t.Contains("per min"));
        return new RateDetection(limited, retry, daily, raw.Trim());
    }

    static int? WaitFromText(string raw)
    {
        var rx = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
        var m = System.Text.RegularExpressions.Regex.Match(raw, "\"retryDelay\"\\s*:\\s*\"(\\d+(?:\\.\\d+)?)s\"", rx);
        if (m.Success) return (int)Math.Ceiling(double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));

        m = System.Text.RegularExpressions.Regex.Match(raw, @"(?:try again|retry|reset[s]?)(?: after| in)?\s*(\d+(?:\.\d+)?)\s*(ms|milliseconds|seconds|secs|sec|s|minutes|mins|min|m)\b", rx);
        if (m.Success)
        {
            var n = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var u = m.Groups[2].Value.ToLowerInvariant();
            var sec = u.StartsWith("ms") || u.StartsWith("milli") ? n / 1000 : u.StartsWith("m") ? n * 60 : n;
            return (int)Math.Ceiling(sec);
        }

        m = System.Text.RegularExpressions.Regex.Match(raw, "X-RateLimit-Reset\"?\\s*[:=]\\s*\"?(\\d{10,13})", rx);
        if (m.Success && long.TryParse(m.Groups[1].Value, out var epoch))
        {
            var secs = m.Groups[1].Value.Length == 13 ? epoch / 1000 : epoch;
            var diff = secs - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (diff > 0 && diff < 86400) return (int)diff;
        }
        return null;
    }

    public static string ExtractError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var e))
            {
                if (e.ValueKind == JsonValueKind.String) return e.GetString() ?? body;
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) &&
                    m.ValueKind == JsonValueKind.String)
                    return m.GetString() ?? body;
            }
        }
        catch
        {
            // not JSON
        }
        return body.Length > 600 ? body[..600] : body;
    }

    /// <summary>Reads a Server-Sent-Events body and yields (eventName, data) pairs.</summary>
    /// <summary>Debug: write the first lines of every streamed model response to the app log (switched on in the settings, until the app is closed).</summary>
    public static bool RawLogOn;
    public static Action<string>? RawLog;

    public static async IAsyncEnumerable<(string? Event, string Data)> ReadSseAsync(
        HttpResponseMessage resp, [EnumeratorCancellation] CancellationToken ct)
    {
        var rawCount = 0;
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        var reader = new StreamReader(stream, Encoding.UTF8);
        try
        {
            string? ev = null;
            var gotData = false;
            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && gotData)
                {
                    // On Android the native HTTP stack can throw a Java exception when the server
                    // closes the connection right after the last chunk. The answer is complete then.
                    yield break;
                }
                if (line == null) yield break;
                if (line.Length == 0) { ev = null; continue; }
                if (line.StartsWith("event:", StringComparison.Ordinal)) ev = line[6..].Trim();
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    gotData = true;
                    if (RawLogOn && rawCount++ < 60) RawLog?.Invoke("RAW " + (line.Length > 400 ? line[..400] + "…" : line));
                    yield return (ev, line[5..].Trim());
                }
            }
        }
        finally
        {
            // Disposing a half-closed native stream may throw; it must never hide the real result.
            try { reader.Dispose(); } catch { }
            try { stream.Dispose(); } catch { }
        }
    }

    /// <summary>Error text with the exception type and the inner messages (native errors often have an empty message).</summary>
    public static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e != null; e = e.InnerException)
        {
            var m = string.IsNullOrWhiteSpace(e.Message) ? "" : e.Message.Trim();
            var t = e.GetType().Name;
            var s = m.Length == 0 ? t : (m.Contains(t) ? m : $"{t}: {m}");
            if (!parts.Contains(s)) parts.Add(s);
        }
        return string.Join(" → ", parts);
    }
}

// ---------------------------------------------------------------- OpenAI-compatible

/// <summary>
/// Works with OpenAI and every service that copies its /chat/completions API:
/// OpenRouter, DeepSeek, Groq, Mistral, xAI, Together, Ollama, LM Studio, ...
/// </summary>
/// <summary>A tool call being assembled from streamed pieces.</summary>
sealed class ToolCallAcc
{
    public string Id = "", Name = "";
    public StringBuilder Args = new();

    /// <summary>Adds the "tool_calls" array of a delta (or of a whole message) to the collection.</summary>
    public static void Collect(SortedDictionary<int, ToolCallAcc> calls, JsonElement tcs)
    {
        if (tcs.ValueKind != JsonValueKind.Array) return;
        foreach (var tc in tcs.EnumerateArray())
        {
            var hasId = tc.TryGetProperty("id", out var idp) && idp.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(idp.GetString());
            int idx;
            if (tc.TryGetProperty("index", out var ix) && ix.ValueKind == JsonValueKind.Number) idx = ix.GetInt32();
            else idx = hasId || calls.Count == 0 ? calls.Count : calls.Keys.Max();
            if (!calls.TryGetValue(idx, out var acc)) calls[idx] = acc = new ToolCallAcc();
            if (hasId) acc.Id = idp.GetString()!;
            if (tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
            {
                if (fn.TryGetProperty("name", out var np) && np.ValueKind == JsonValueKind.String && acc.Name.Length == 0)
                    acc.Name = np.GetString() ?? "";
                if (fn.TryGetProperty("arguments", out var ap))
                {
                    if (ap.ValueKind == JsonValueKind.String) acc.Args.Append(ap.GetString());
                    else if (ap.ValueKind == JsonValueKind.Object) acc.Args.Append(ap.GetRawText());
                }
            }
        }
    }
}

public class OpenAiCompatibleProvider : IChatProvider
{
    readonly HttpClient _http;
    public OpenAiCompatibleProvider(HttpClient http) => _http = http;

    public async IAsyncEnumerable<string> StreamAsync(ProviderConfig cfg, string model,
        IReadOnlyList<ChatMessage> history, string? systemPrompt, Func<string, byte[], Task>? onImage, [EnumeratorCancellation] CancellationToken ct)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = systemPrompt });
        foreach (var t in HistoryHelper.Build(history))
        {
            JsonNode content;
            if (t.Images.Count == 0)
            {
                content = JsonValue.Create(t.Text)!;
            }
            else
            {
                var parts = new JsonArray();
                if (t.Text.Length > 0)
                    parts.Add(new JsonObject { ["type"] = "text", ["text"] = t.Text });
                foreach (var (mime, b64) in t.Images)
                    parts.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = $"data:{mime};base64,{b64}" }
                    });
                content = parts;
            }
            messages.Add(new JsonObject { ["role"] = t.Role, ["content"] = content });
        }

        // Tools: the model may ask for functions; we run them and send the results back (up to MaxRounds times).
        var tools = cfg.Tools is { Tools.Count: > 0 } ? cfg.Tools : null;
        var useTools = tools != null;
        var nudged = false;

        for (var round = 0; ; round++)
        {
            var body = new JsonObject { ["model"] = model, ["stream"] = !cfg.NoStream, ["messages"] = messages.DeepClone() };
            ProviderHttp.ApplyGen(body, cfg);
            // after the last allowed round the tools are left out, so the model has to answer
            if (useTools && tools != null && round < tools.MaxRounds) body["tools"] = tools.ToOpenAi();

            HttpResponseMessage? resp = null;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, ProviderHttp.Url(cfg, ProviderHttp.ChatPath(cfg, "chat/completions")))
                {
                    Content = ProviderHttp.JsonBody(ProviderHttp.MergeBody(body, cfg))
                };
                if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey.Trim());

                resp = await _http.SendAsync(ProviderHttp.Customize(req, cfg), HttpCompletionOption.ResponseHeadersRead, ct);
                try { await ProviderHttp.EnsureSuccessAsync(resp, ct); }
                catch { resp.Dispose(); resp = null; throw; }
            }
            catch (HttpRequestException ex) when (useTools && round == 0 && ProviderHttp.LooksLikeToolRejection(ex.Message))
            {
                resp = null;   // this model/API does not accept tools: answer without them
            }
            if (resp == null)
            {
                useTools = false;
                tools = null;
                yield return ProviderStatus.Prefix + "⚠ tools are not supported by this model; answering without them";
                continue;
            }
            using var _respGuard = new SafeDispose(resp);

            var said = new StringBuilder();
            var calls = new SortedDictionary<int, ToolCallAcc>();

            if (cfg.NoStream)
            {
                var whole = await resp.Content.ReadAsStringAsync(ct);
                string? full = null;
                var imageUrls = new List<string>();
                using (var doc = JsonDocument.Parse(whole))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("error", out _))
                        throw new HttpRequestException(ProviderHttp.ExtractError(whole));
                    if (root.TryGetProperty("choices", out var choices) &&
                        choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
                        choices[0].TryGetProperty("message", out var msg))
                    {
                        if (msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                            full = c.GetString();
                        if (msg.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
                            foreach (var im in imgs.EnumerateArray())
                                if (im.TryGetProperty("image_url", out var iu) &&
                                    iu.TryGetProperty("url", out var u) && u.GetString() is { Length: > 0 } url)
                                    imageUrls.Add(url);
                        if (msg.TryGetProperty("tool_calls", out var tcs))
                            ToolCallAcc.Collect(calls, tcs);
                    }
                }
                if (!string.IsNullOrEmpty(full)) { said.Append(full); yield return full; }
                foreach (var url in imageUrls)
                {
                    var md = await ImageHelper.HandleUrlAsync(url, onImage);
                    if (md != null) yield return md;
                }
            }
            else
            {
                var inReason = false;
                var reasonAll = new StringBuilder();
                var reasonParts = new List<StringBuilder>();   // each time the thinking box is opened again, a new part
                await foreach (var (_, data) in ProviderHttp.ReadSseAsync(resp, ct))
                {
                    if (data == "[DONE]") break;

                    string? text = null, reason = null;
                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("error", out var err))
                            throw new HttpRequestException(ProviderHttp.ExtractError(data));

                        if (root.TryGetProperty("choices", out var choices) &&
                            choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
                            choices[0].TryGetProperty("delta", out var delta))
                        {
                            if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                                text = c.GetString();

                            // thinking models (LM Studio, DeepSeek, ...) stream their reasoning separately
                            if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                                reason = rc.GetString();
                            else if (delta.TryGetProperty("reasoning", out var rs) && rs.ValueKind == JsonValueKind.String)
                                reason = rs.GetString();

                            if (delta.TryGetProperty("tool_calls", out var tcs))
                                ToolCallAcc.Collect(calls, tcs);

                            // Image output (e.g. OpenRouter image models): delta.images[].image_url.url
                            if (delta.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var im in imgs.EnumerateArray())
                                {
                                    if (im.TryGetProperty("image_url", out var iu) &&
                                        iu.TryGetProperty("url", out var u) && u.GetString() is { Length: > 0 } url)
                                    {
                                        var md = await ImageHelper.HandleUrlAsync(url, onImage);
                                        if (md != null) text = (text ?? "") + md;
                                    }
                                }
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(reason))
                    {
                        if (!inReason) { yield return "<think>"; inReason = true; reasonParts.Add(new StringBuilder()); }
                        reasonAll.Append(reason);
                        reasonParts[^1].Append(reason);
                        yield return reason;
                    }
                    if (!string.IsNullOrEmpty(text))
                    {
                        if (inReason) { yield return "</think>\n\n"; inReason = false; }
                        said.Append(text);
                        yield return text;
                    }
                }
                if (inReason) yield return "</think>\n\n";

                // some models (e.g. certain DeepSeek versions) write the whole answer as "reasoning" and leave the answer empty:
                // then the reasoning IS the answer (the thinking box is hidden when it equals the answer)
                if (said.ToString().Trim().Length == 0 && calls.Count == 0 && reasonAll.ToString().Trim() is { Length: > 0 } onlyReason)
                {
                    // when the reasoning came in several parts (thoughts, a blank answer, then the real answer labelled as reasoning), the last part is the answer
                    var answer = reasonParts.Count >= 2 ? reasonParts[^1].ToString().Trim() : onlyReason;
                    if (answer.Length == 0) answer = onlyReason;
                    said.Append(answer);
                    yield return answer;
                }
            }

            // some local models answer with nothing after a tool result: ask once more for the answer
            if (calls.Count == 0 && round > 0 && !nudged && useTools && said.ToString().Trim().Length == 0)
            {
                nudged = true;
                messages.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = "Now write your answer to my last message, using the tool results above."
                });
                continue;
            }

            if (calls.Count == 0 || tools == null || !useTools || round >= tools.MaxRounds) yield break;

            // run the requested tools and ask again
            var callArr = new JsonArray();
            var toolMsgs = new List<JsonObject>();
            foreach (var (idx, acc) in calls)
            {
                if (string.IsNullOrEmpty(acc.Id)) acc.Id = "call_" + round + "_" + idx;
                if (string.IsNullOrEmpty(acc.Name)) continue;
                var args = acc.Args.Length > 0 ? acc.Args.ToString() : "{}";
                callArr.Add(new JsonObject
                {
                    ["id"] = acc.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = acc.Name, ["arguments"] = args }
                });

                if (tools.ShowCalls)
                    yield return (said.Length > 0 && !said.ToString().EndsWith("\n\n") ? "\n\n" : "") + tools.CallLine(acc.Name, args);
                yield return ProviderStatus.Prefix + "🔧 " + (tools.Find(acc.Name)?.Label ?? acc.Name) + "...";
                said.Append("\n\n");

                var result = await tools.ExecuteAsync(acc.Name, args, ct);
                toolMsgs.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = acc.Id, ["content"] = result });
            }
            if (callArr.Count == 0) yield break;

            var asst = new JsonObject { ["role"] = "assistant", ["tool_calls"] = callArr };
            var sayText = said.ToString().Trim();
            asst["content"] = sayText.Length > 0 ? sayText : null;
            messages.Add(asst);
            foreach (var m in toolMsgs) messages.Add(m);
        }
    }

    public async Task<List<string>> ListModelsAsync(ProviderConfig cfg, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ProviderHttp.Url(cfg, ProviderHttp.ModelsPath(cfg, "models")));
        if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey.Trim());

        var resp = await _http.SendAsync(ProviderHttp.Customize(req, cfg), ct);
        using var _respGuard = new SafeDispose(resp);
        await ProviderHttp.EnsureSuccessAsync(resp, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        var list = new List<string>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var item in data.EnumerateArray())
                if (item.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } s)
                    list.Add(s);

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }
}

// ---------------------------------------------------------------- Anthropic (Claude)

public class AnthropicProvider : IChatProvider
{
    const string Version = "2023-06-01";
    readonly HttpClient _http;
    public AnthropicProvider(HttpClient http) => _http = http;

    public async IAsyncEnumerable<string> StreamAsync(ProviderConfig cfg, string model,
        IReadOnlyList<ChatMessage> history, string? systemPrompt, Func<string, byte[], Task>? onImage, [EnumeratorCancellation] CancellationToken ct)
    {
        var messages = new JsonArray();
        foreach (var t in HistoryHelper.Build(history, nativePdf: true))
        {
            var parts = new JsonArray();
            foreach (var (mime, b64) in t.Images)
                parts.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mime, ["data"] = b64 }
                });
            foreach (var pdf in t.Pdfs)
                parts.Add(new JsonObject
                {
                    ["type"] = "document",
                    ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "application/pdf", ["data"] = pdf }
                });
            if (t.Text.Length > 0)
                parts.Add(new JsonObject { ["type"] = "text", ["text"] = t.Text });
            messages.Add(new JsonObject { ["role"] = t.Role, ["content"] = parts });
        }

        var tools = cfg.Tools is { Tools.Count: > 0 } ? cfg.Tools : null;

        for (var round = 0; ; round++)
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["max_tokens"] = cfg.MaxTokens ?? 8192,
                ["stream"] = true,
                ["messages"] = messages.DeepClone()
            };
            if (cfg.Temperature != null) body["temperature"] = cfg.Temperature;
            if (cfg.TopP != null) body["top_p"] = cfg.TopP;
            if (!string.IsNullOrWhiteSpace(systemPrompt)) body["system"] = systemPrompt;
            if (tools != null && round < tools.MaxRounds) body["tools"] = tools.ToAnthropic();

            using var req = new HttpRequestMessage(HttpMethod.Post, ProviderHttp.Url(cfg, ProviderHttp.ChatPath(cfg, "messages")))
            {
                Content = ProviderHttp.JsonBody(ProviderHttp.MergeBody(body, cfg))
            };
            req.Headers.Add("x-api-key", cfg.ApiKey.Trim());
            req.Headers.Add("anthropic-version", Version);

            var resp = await _http.SendAsync(ProviderHttp.Customize(req, cfg), HttpCompletionOption.ResponseHeadersRead, ct);
            using var _respGuard = new SafeDispose(resp);
            await ProviderHttp.EnsureSuccessAsync(resp, ct);

            var said = new StringBuilder();
            var uses = new SortedDictionary<int, ToolCallAcc>();   // tool_use blocks by their index

            await foreach (var (_, data) in ProviderHttp.ReadSseAsync(resp, ct))
            {
                string? text = null;
                bool stop = false;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;
                    var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                    var index = root.TryGetProperty("index", out var ixp) && ixp.ValueKind == JsonValueKind.Number ? ixp.GetInt32() : -1;

                    if (type == "error")
                        throw new HttpRequestException(ProviderHttp.ExtractError(data));

                    if (type == "message_stop") stop = true;
                    else if (type == "content_block_start" && root.TryGetProperty("content_block", out var cb) &&
                             cb.TryGetProperty("type", out var cbt) && cbt.GetString() == "tool_use")
                    {
                        uses[index] = new ToolCallAcc
                        {
                            Id = cb.TryGetProperty("id", out var cid) ? cid.GetString() ?? "" : "",
                            Name = cb.TryGetProperty("name", out var cn) ? cn.GetString() ?? "" : ""
                        };
                    }
                    else if (type == "content_block_delta" &&
                             root.TryGetProperty("delta", out var delta) &&
                             delta.TryGetProperty("type", out var dt))
                    {
                        if (dt.GetString() == "text_delta" && delta.TryGetProperty("text", out var tx))
                            text = tx.GetString();
                        else if (dt.GetString() == "input_json_delta" && uses.TryGetValue(index, out var u) &&
                                 delta.TryGetProperty("partial_json", out var pj))
                            u.Args.Append(pj.GetString());
                    }
                }
                catch (JsonException)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(text)) { said.Append(text); yield return text; }
                if (stop) break;
            }

            if (uses.Count == 0 || tools == null || round >= tools.MaxRounds) yield break;

            // the assistant turn (text + tool_use blocks), then our tool_result blocks
            var assistant = new JsonArray();
            if (said.ToString().Trim().Length > 0) assistant.Add(new JsonObject { ["type"] = "text", ["text"] = said.ToString().Trim() });
            var results = new JsonArray();
            foreach (var (_, use) in uses)
            {
                var args = use.Args.Length > 0 ? use.Args.ToString() : "{}";
                assistant.Add(new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = use.Id,
                    ["name"] = use.Name,
                    ["input"] = ToolSet.ParseArgs(args)
                });

                if (tools.ShowCalls)
                    yield return (said.Length > 0 && !said.ToString().EndsWith("\n\n") ? "\n\n" : "") + tools.CallLine(use.Name, args);
                yield return ProviderStatus.Prefix + "🔧 " + (tools.Find(use.Name)?.Label ?? use.Name) + "...";
                said.Append("\n\n");

                var result = await tools.ExecuteAsync(use.Name, args, ct);
                results.Add(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = use.Id, ["content"] = result });
            }
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = assistant });
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = results });
        }
    }

    public async Task<List<string>> ListModelsAsync(ProviderConfig cfg, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ProviderHttp.Url(cfg, ProviderHttp.ModelsPath(cfg, "models?limit=1000")));
        req.Headers.Add("x-api-key", cfg.ApiKey.Trim());
        req.Headers.Add("anthropic-version", Version);

        var resp = await _http.SendAsync(ProviderHttp.Customize(req, cfg), ct);
        using var _respGuard = new SafeDispose(resp);
        await ProviderHttp.EnsureSuccessAsync(resp, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        var list = new List<string>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var item in data.EnumerateArray())
                if (item.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } s)
                    list.Add(s);
        return list;
    }
}

// ---------------------------------------------------------------- Google Gemini

public class GeminiProvider : IChatProvider
{
    readonly HttpClient _http;
    public GeminiProvider(HttpClient http) => _http = http;

    static string CleanModel(string model) => model.StartsWith("models/") ? model["models/".Length..] : model;

    public async IAsyncEnumerable<string> StreamAsync(ProviderConfig cfg, string model,
        IReadOnlyList<ChatMessage> history, string? systemPrompt, Func<string, byte[], Task>? onImage, [EnumeratorCancellation] CancellationToken ct)
    {
        var contents = new JsonArray();
        foreach (var t in HistoryHelper.Build(history, nativePdf: true))
        {
            var parts = new JsonArray();
            foreach (var (mime, b64) in t.Images)
                parts.Add(new JsonObject
                {
                    ["inlineData"] = new JsonObject { ["mimeType"] = mime, ["data"] = b64 }
                });
            foreach (var pdf in t.Pdfs)
                parts.Add(new JsonObject
                {
                    ["inlineData"] = new JsonObject { ["mimeType"] = "application/pdf", ["data"] = pdf }
                });
            if (t.Text.Length > 0)
                parts.Add(new JsonObject { ["text"] = t.Text });

            contents.Add(new JsonObject
            {
                ["role"] = t.Role == "assistant" ? "model" : "user",
                ["parts"] = parts
            });
        }

        var body = new JsonObject { ["contents"] = contents };
        if (cfg.Temperature != null || cfg.TopP != null || cfg.MaxTokens != null)
        {
            var gc = new JsonObject();
            if (cfg.Temperature != null) gc["temperature"] = cfg.Temperature;
            if (cfg.TopP != null) gc["topP"] = cfg.TopP;
            if (cfg.MaxTokens != null) gc["maxOutputTokens"] = cfg.MaxTokens;
            body["generationConfig"] = gc;
        }
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            body["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = systemPrompt })
            };
        }

        var tools = cfg.Tools is { Tools.Count: > 0 } ? cfg.Tools : null;

        for (var round = 0; ; round++)
        {
            if (tools != null && round < tools.MaxRounds) body["tools"] = tools.ToGemini();
            else body.Remove("tools");

            var url = ProviderHttp.Url(cfg, ProviderHttp.ChatPath(cfg, $"models/{CleanModel(model)}:streamGenerateContent?alt=sse", CleanModel(model)));
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = ProviderHttp.JsonBody(ProviderHttp.MergeBody(body, cfg)) };
            req.Headers.Add("x-goog-api-key", cfg.ApiKey.Trim());

            var resp = await _http.SendAsync(ProviderHttp.Customize(req, cfg), HttpCompletionOption.ResponseHeadersRead, ct);
            using var _respGuard = new SafeDispose(resp);
            await ProviderHttp.EnsureSuccessAsync(resp, ct);

            var said = new StringBuilder();
            var callParts = new List<JsonNode>();   // functionCall parts exactly as the model sent them (they may carry a signature)

            await foreach (var (_, data) in ProviderHttp.ReadSseAsync(resp, ct))
            {
                string? text = null;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("error", out _))
                        throw new HttpRequestException(ProviderHttp.ExtractError(data));

                    if (root.TryGetProperty("candidates", out var cands) &&
                        cands.ValueKind == JsonValueKind.Array && cands.GetArrayLength() > 0 &&
                        cands[0].TryGetProperty("content", out var content) &&
                        content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                    {
                        var sb = new StringBuilder();
                        foreach (var part in parts.EnumerateArray())
                        {
                            if (part.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True) continue;
                            if (part.TryGetProperty("functionCall", out _) || part.TryGetProperty("function_call", out _))
                            {
                                if (JsonNode.Parse(part.GetRawText()) is JsonNode fc) callParts.Add(fc);
                                continue;
                            }
                            if (part.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                                sb.Append(tx.GetString());

                            // Image output (Gemini image models): inlineData { mimeType, data }
                            if ((part.TryGetProperty("inlineData", out var inl) || part.TryGetProperty("inline_data", out inl)) &&
                                inl.ValueKind == JsonValueKind.Object && onImage != null)
                            {
                                var mime = inl.TryGetProperty("mimeType", out var mt) ? mt.GetString()
                                    : inl.TryGetProperty("mime_type", out var mt2) ? mt2.GetString() : null;
                                if (inl.TryGetProperty("data", out var d) && d.GetString() is { Length: > 0 } b64)
                                {
                                    try { await onImage(mime ?? "image/png", Convert.FromBase64String(b64)); }
                                    catch (FormatException) { }
                                }
                            }
                        }
                        text = sb.ToString();
                    }
                }
                catch (JsonException)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(text)) { said.Append(text); yield return text; }
            }

            if (callParts.Count == 0 || tools == null || round >= tools.MaxRounds) yield break;

            var modelParts = new JsonArray();
            if (said.ToString().Trim().Length > 0) modelParts.Add(new JsonObject { ["text"] = said.ToString().Trim() });
            var responseParts = new JsonArray();
            foreach (var part in callParts)
            {
                var fc = (part["functionCall"] ?? part["function_call"]) as JsonObject;
                var name = fc?["name"]?.GetValue<string>() ?? "";
                var args = fc?["args"]?.ToJsonString() ?? "{}";
                var id = fc?["id"]?.GetValue<string>();
                modelParts.Add(part.DeepClone());

                if (tools.ShowCalls)
                    yield return (said.Length > 0 && !said.ToString().EndsWith("\n\n") ? "\n\n" : "") + tools.CallLine(name, args);
                yield return ProviderStatus.Prefix + "🔧 " + (tools.Find(name)?.Label ?? name) + "...";
                said.Append("\n\n");

                var result = await tools.ExecuteAsync(name, args, ct);
                var fr = new JsonObject { ["name"] = name, ["response"] = new JsonObject { ["result"] = result } };
                if (!string.IsNullOrEmpty(id)) fr["id"] = id;
                responseParts.Add(new JsonObject { ["functionResponse"] = fr });
            }
            contents.Add(new JsonObject { ["role"] = "model", ["parts"] = modelParts });
            contents.Add(new JsonObject { ["role"] = "user", ["parts"] = responseParts });
        }
    }

    public async Task<List<string>> ListModelsAsync(ProviderConfig cfg, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ProviderHttp.Url(cfg, ProviderHttp.ModelsPath(cfg, "models?pageSize=1000")));
        req.Headers.Add("x-goog-api-key", cfg.ApiKey.Trim());

        var resp = await _http.SendAsync(ProviderHttp.Customize(req, cfg), ct);
        using var _respGuard = new SafeDispose(resp);
        await ProviderHttp.EnsureSuccessAsync(resp, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        var list = new List<string>();
        if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in models.EnumerateArray())
            {
                if (!m.TryGetProperty("name", out var n) || n.GetString() is not { Length: > 0 } name) continue;

                // keep only models that can chat
                if (m.TryGetProperty("supportedGenerationMethods", out var methods) &&
                    methods.ValueKind == JsonValueKind.Array &&
                    !methods.EnumerateArray().Any(x => x.GetString() == "generateContent"))
                    continue;

                list.Add(CleanModel(name));
            }
        }
        return list;
    }
}


// ---------------------------------------------------------------- Fooocus-API (image generation)

/// <summary>
/// Talks to a local Fooocus-API server. Every message is an image description: the last user message is the prompt,
/// the model is the checkpoint file name, and the generated PNG files are handed to the chat through onImage.
/// </summary>
public class FooocusApiProvider : IChatProvider
{
    readonly HttpClient _http;
    public FooocusApiProvider(HttpClient http) => _http = http;

    static HttpRequestMessage NewRequest(HttpMethod method, ProviderConfig cfg, string path, HttpContent? content = null)
    {
        var req = new HttpRequestMessage(method, ProviderHttp.Url(cfg, path)) { Content = content };
        if (!string.IsNullOrWhiteSpace(cfg.ApiKey)) req.Headers.TryAddWithoutValidation("X-API-KEY", cfg.ApiKey.Trim());
        return req;
    }

    public async IAsyncEnumerable<string> StreamAsync(ProviderConfig cfg, string model,
        IReadOnlyList<ChatMessage> history, string? systemPrompt, Func<string, byte[], Task>? onImage,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var prompt = history.LastOrDefault(m => m.Role == "user")?.Content?.Trim();
        if (string.IsNullOrEmpty(prompt)) throw new InvalidOperationException("Write a description of the image first.");
        if (onImage == null) throw new InvalidOperationException("Images cannot be shown here.");

        var styles = new JsonArray();
        foreach (var st in cfg.ImageStyles.Split(new[] { ',', '،', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            styles.Add(st);

        var count = Math.Clamp(cfg.ImageCount, 1, 4);
        for (var i = 0; i < count; i++)
        {
            var body = new JsonObject
            {
                ["prompt"] = prompt,
                ["negative_prompt"] = cfg.NegativePrompt ?? "",
                ["style_selections"] = JsonNode.Parse(styles.ToJsonString()),
                ["performance_selection"] = string.IsNullOrWhiteSpace(cfg.ImagePerformance) ? "Speed" : cfg.ImagePerformance,
                ["aspect_ratios_selection"] = string.IsNullOrWhiteSpace(cfg.ImageAspect) ? "1152*896" : cfg.ImageAspect,
                ["image_number"] = 1,
                ["image_seed"] = -1,
                ["sharpness"] = 2,
                ["guidance_scale"] = 4,
                ["refiner_model_name"] = "None",
                ["refiner_switch"] = 0.5,
                ["save_meta"] = true,
                ["save_extension"] = "png",
                ["require_base64"] = false,
                ["async_process"] = false
            };
            if (!string.IsNullOrWhiteSpace(model)) body["base_model_name"] = model;

            using var req = NewRequest(HttpMethod.Post, cfg, "v1/generation/text-to-image?accept_query=image/png", ProviderHttp.JsonBody(body));
            req.Headers.TryAddWithoutValidation("Accept", "image/png");

            var resp = await _http.SendAsync(ProviderHttp.Customize(req, cfg), HttpCompletionOption.ResponseContentRead, ct);
            using var _respGuard = new SafeDispose(resp);
            await ProviderHttp.EnsureSuccessAsync(resp, ct);

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";

            if (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                await onImage(type, bytes);
                continue;
            }

            // The server answered with JSON instead of the raw image (older versions): base64 or a url per image.
            using var doc = JsonDocument.Parse(bytes);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : new List<JsonElement> { doc.RootElement };
            var got = false;
            foreach (var it in items)
            {
                if (it.ValueKind != JsonValueKind.Object) continue;
                if (it.TryGetProperty("base64", out var b64) && b64.ValueKind == JsonValueKind.String && b64.GetString() is { Length: > 0 } data)
                {
                    await onImage("image/png", Convert.FromBase64String(data));
                    got = true;
                }
                else if (it.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String && u.GetString() is { Length: > 0 } url)
                {
                    using var imgReq = NewRequest(HttpMethod.Get, cfg, url);
                    var imgResp = await _http.SendAsync(ProviderHttp.Customize(imgReq, cfg), ct);
                    using var _imgGuard = new SafeDispose(imgResp);
                    await ProviderHttp.EnsureSuccessAsync(imgResp, ct);
                    await onImage(imgResp.Content.Headers.ContentType?.MediaType ?? "image/png", await imgResp.Content.ReadAsByteArrayAsync(ct));
                    got = true;
                }
            }
            if (!got) throw new InvalidOperationException("The image server returned no image: " + Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 300)));
        }

        yield break;
    }

    public async Task<List<string>> ListModelsAsync(ProviderConfig cfg, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Get, cfg, "v1/engines/all-models");
        var resp = await _http.SendAsync(ProviderHttp.Customize(req, cfg), ct);
        using var _respGuard = new SafeDispose(resp);
        await ProviderHttp.EnsureSuccessAsync(resp, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        var list = new List<string>();
        if (doc.RootElement.TryGetProperty("model_filenames", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var m in arr.EnumerateArray())
                if (m.GetString() is { Length: > 0 } name) list.Add(name);
        return list;
    }
}
