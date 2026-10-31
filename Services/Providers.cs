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

    public ProviderFactory(HttpClient http)
    {
        _openAi = new OpenAiCompatibleProvider(http);
        _anthropic = new AnthropicProvider(http);
        _gemini = new GeminiProvider(http);
    }

    public IChatProvider Create(ProviderType type) => type switch
    {
        ProviderType.Anthropic => _anthropic,
        ProviderType.Gemini => _gemini,
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
            var text = m.Content ?? "";
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

    public static StringContent JsonBody(JsonNode node) =>
        new(node.ToJsonString(), Encoding.UTF8, "application/json");

    public static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {ExtractError(body)}");
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
    public static async IAsyncEnumerable<(string? Event, string Data)> ReadSseAsync(
        HttpResponseMessage resp, [EnumeratorCancellation] CancellationToken ct)
    {
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

        var body = new JsonObject { ["model"] = model, ["stream"] = true, ["messages"] = messages };

        using var req = new HttpRequestMessage(HttpMethod.Post, ProviderHttp.Url(cfg, "chat/completions"))
        {
            Content = ProviderHttp.JsonBody(body)
        };
        if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey.Trim());

        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        using var _respGuard = new SafeDispose(resp);
        await ProviderHttp.EnsureSuccessAsync(resp, ct);

        await foreach (var (_, data) in ProviderHttp.ReadSseAsync(resp, ct))
        {
            if (data == "[DONE]") yield break;

            string? text = null;
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

            if (!string.IsNullOrEmpty(text)) yield return text;
        }
    }

    public async Task<List<string>> ListModelsAsync(ProviderConfig cfg, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ProviderHttp.Url(cfg, "models"));
        if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey.Trim());

        var resp = await _http.SendAsync(req, ct);
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

        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = 8192,
            ["stream"] = true,
            ["messages"] = messages
        };
        if (!string.IsNullOrWhiteSpace(systemPrompt)) body["system"] = systemPrompt;

        using var req = new HttpRequestMessage(HttpMethod.Post, ProviderHttp.Url(cfg, "messages"))
        {
            Content = ProviderHttp.JsonBody(body)
        };
        req.Headers.Add("x-api-key", cfg.ApiKey.Trim());
        req.Headers.Add("anthropic-version", Version);

        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        using var _respGuard = new SafeDispose(resp);
        await ProviderHttp.EnsureSuccessAsync(resp, ct);

        await foreach (var (_, data) in ProviderHttp.ReadSseAsync(resp, ct))
        {
            string? text = null;
            bool stop = false;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

                if (type == "error")
                    throw new HttpRequestException(ProviderHttp.ExtractError(data));

                if (type == "message_stop") stop = true;
                else if (type == "content_block_delta" &&
                         root.TryGetProperty("delta", out var delta) &&
                         delta.TryGetProperty("type", out var dt) && dt.GetString() == "text_delta" &&
                         delta.TryGetProperty("text", out var tx))
                {
                    text = tx.GetString();
                }
            }
            catch (JsonException)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(text)) yield return text;
            if (stop) yield break;
        }
    }

    public async Task<List<string>> ListModelsAsync(ProviderConfig cfg, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ProviderHttp.Url(cfg, "models?limit=1000"));
        req.Headers.Add("x-api-key", cfg.ApiKey.Trim());
        req.Headers.Add("anthropic-version", Version);

        var resp = await _http.SendAsync(req, ct);
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
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            body["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = systemPrompt })
            };
        }

        var url = ProviderHttp.Url(cfg, $"models/{CleanModel(model)}:streamGenerateContent?alt=sse");
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = ProviderHttp.JsonBody(body) };
        req.Headers.Add("x-goog-api-key", cfg.ApiKey.Trim());

        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        using var _respGuard = new SafeDispose(resp);
        await ProviderHttp.EnsureSuccessAsync(resp, ct);

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

            if (!string.IsNullOrEmpty(text)) yield return text;
        }
    }

    public async Task<List<string>> ListModelsAsync(ProviderConfig cfg, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ProviderHttp.Url(cfg, "models?pageSize=1000"));
        req.Headers.Add("x-goog-api-key", cfg.ApiKey.Trim());

        var resp = await _http.SendAsync(req, ct);
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
