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
        string? systemPrompt, CancellationToken ct);

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
    /// <summary>
    /// Turns the stored messages into a clean (role, content) list:
    /// drops empty/failed replies, merges consecutive messages from the same role,
    /// and makes sure the list starts with a user message (Claude and Gemini require that).
    /// </summary>
    public static List<(string Role, string Content)> Build(IReadOnlyList<ChatMessage> history)
    {
        var result = new List<(string Role, string Content)>();
        foreach (var m in history)
        {
            if (string.IsNullOrWhiteSpace(m.Content)) continue;
            if (result.Count > 0 && result[^1].Role == m.Role)
                result[^1] = (m.Role, result[^1].Content + "\n\n" + m.Content);
            else
                result.Add((m.Role, m.Content));
        }
        while (result.Count > 0 && result[0].Role != "user") result.RemoveAt(0);
        return result;
    }
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
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? ev = null;
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) yield break;
            if (line.Length == 0) { ev = null; continue; }
            if (line.StartsWith("event:", StringComparison.Ordinal)) ev = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal)) yield return (ev, line[5..].Trim());
        }
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
        IReadOnlyList<ChatMessage> history, string? systemPrompt, [EnumeratorCancellation] CancellationToken ct)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = systemPrompt });
        foreach (var (role, content) in HistoryHelper.Build(history))
            messages.Add(new JsonObject { ["role"] = role, ["content"] = content });

        var body = new JsonObject { ["model"] = model, ["stream"] = true, ["messages"] = messages };

        using var req = new HttpRequestMessage(HttpMethod.Post, ProviderHttp.Url(cfg, "chat/completions"))
        {
            Content = ProviderHttp.JsonBody(body)
        };
        if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey.Trim());

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
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
                    choices[0].TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    text = c.GetString();
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

        using var resp = await _http.SendAsync(req, ct);
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
        IReadOnlyList<ChatMessage> history, string? systemPrompt, [EnumeratorCancellation] CancellationToken ct)
    {
        var messages = new JsonArray();
        foreach (var (role, content) in HistoryHelper.Build(history))
            messages.Add(new JsonObject { ["role"] = role, ["content"] = content });

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

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
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

        using var resp = await _http.SendAsync(req, ct);
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
        IReadOnlyList<ChatMessage> history, string? systemPrompt, [EnumeratorCancellation] CancellationToken ct)
    {
        var contents = new JsonArray();
        foreach (var (role, content) in HistoryHelper.Build(history))
        {
            contents.Add(new JsonObject
            {
                ["role"] = role == "assistant" ? "model" : "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = content })
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

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
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

        using var resp = await _http.SendAsync(req, ct);
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
