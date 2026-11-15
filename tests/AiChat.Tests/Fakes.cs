using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AiChat.Models;
using AiChat.Services;

namespace AiChat.Tests;

/// <summary>An HttpMessageHandler that answers from a function and records every request.</summary>
public sealed class FakeHandler : HttpMessageHandler
{
    public sealed record Seen(HttpMethod Method, string Url, string Body, string? ContentType,
        Dictionary<string, string> Headers);

    readonly Func<Seen, HttpResponseMessage> _reply;
    public List<Seen> Requests { get; } = new();

    public FakeHandler(Func<Seen, HttpResponseMessage> reply) => _reply = reply;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var seen = new Seen(request.Method, request.RequestUri!.ToString(), body,
            request.Content?.Headers.ContentType?.ToString(), headers);
        Requests.Add(seen);
        return _reply(seen);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Bytes(byte[] data, string mime) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) { Headers = { { "Content-Type", mime } } } };

    public static HttpResponseMessage Text(string text, string mime = "text/event-stream") =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, mime) };
}

public static class Helpers
{
    public static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };

    public static ProviderConfig Cfg(string connectorJson, string baseUrl = "http://api.test", string key = "", Dictionary<string, string>? inputs = null) =>
        new()
        {
            Type = ProviderType.Custom,
            BaseUrl = baseUrl,
            ApiKey = key,
            ConnectorJson = connectorJson,
            ConnectorInputs = inputs ?? new()
        };

    public static List<ChatMessage> User(string text, params Attachment[] atts)
    {
        var m = new ChatMessage { Role = "user", Content = text };
        m.Attachments.AddRange(atts);
        return new List<ChatMessage> { m };
    }

    public static Attachment Image(byte[] bytes)
    {
        var a = new Attachment { Name = "x.png", Kind = "image", MimeType = "image/png", Size = bytes.Length };
        StorageService.Files[a.Id] = bytes;
        return a;
    }

    public sealed record Run(List<string> Texts, List<(string Mime, byte[] Data)> Images);

    public static async Task<Run> RunAsync(FakeHandler handler, ProviderConfig cfg, List<ChatMessage> history, string model = "m1", string? system = null)
    {
        var provider = new ConnectorProvider(new HttpClient(handler));
        var images = new List<(string, byte[])>();
        var texts = new List<string>();
        await foreach (var t in provider.StreamAsync(cfg, model, history, system,
            (mime, data) => { images.Add((mime, data)); return Task.CompletedTask; }, CancellationToken.None))
            texts.Add(t);
        return new Run(texts, images);
    }

    public static JsonNode J(string json) => JsonNode.Parse(json)!;
}
