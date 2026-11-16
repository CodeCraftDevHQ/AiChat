using System.Text.Json.Nodes;
using AiChat.Models;
using AiChat.Services;
using static AiChat.Tests.Helpers;

namespace AiChat.Tests;

public class GenSettingsTests
{
    [Fact]
    public void Later_layers_override_and_unset_values_fall_through()
    {
        var global = new GenSettings { Temperature = 0.7, TopP = 0.9, MaxTokens = 1000 };
        var api = new GenSettings { Temperature = 0.2 };
        var chat = new GenSettings { MaxTokens = 50 };
        var r = GenSettings.Resolve(global, api, chat);
        Assert.Equal(0.2, r.Temperature);
        Assert.Equal(0.9, r.TopP);
        Assert.Equal(50, r.MaxTokens);
        Assert.Null(GenSettings.Resolve(null, new GenSettings()).Temperature);
    }

    [Fact]
    public void IsEmpty_and_Copy()
    {
        Assert.True(new GenSettings().IsEmpty);
        Assert.False(new GenSettings { SystemPrompt = "x" }.IsEmpty);
        var a = new GenSettings { Temperature = 1 };
        var b = a.Copy();
        b.Temperature = 2;
        Assert.Equal(1, a.Temperature);
    }

    [Fact]
    public void Provider_clone_keeps_gen_values_independent()
    {
        var p = new ProviderConfig { Gen = new GenSettings { MaxTokens = 5 }, Temperature = 0.3 };
        var c = p.Clone();
        c.Gen.MaxTokens = 9;
        Assert.Equal(5, p.Gen.MaxTokens);
        Assert.Equal(0.3, c.Temperature);
    }

    static async Task<JsonNode> Body(ProviderType type, string baseUrl, ProviderConfig cfg, string sse = "data: [DONE]\n\n")
    {
        var h = new FakeHandler(_ => FakeHandler.Text(sse));
        cfg.Type = type;
        cfg.BaseUrl = baseUrl;
        cfg.ApiKey = "k";
        IChatProvider p = type switch
        {
            ProviderType.Anthropic => new AnthropicProvider(new HttpClient(h)),
            ProviderType.Gemini => new GeminiProvider(new HttpClient(h)),
            _ => new OpenAiCompatibleProvider(new HttpClient(h))
        };
        try { await foreach (var _ in p.StreamAsync(cfg, "m", User("hi"), null, null, CancellationToken.None)) { } }
        catch { /* the fake sends no real answer; only the request body matters */ }
        return J(h.Requests[0].Body);
    }

    [Fact]
    public async Task OpenAi_body_has_the_values_only_when_set()
    {
        var none = await Body(ProviderType.OpenAiCompatible, "http://x/v1", new ProviderConfig());
        Assert.Null(none["temperature"]);
        Assert.Null(none["max_tokens"]);

        var set = await Body(ProviderType.OpenAiCompatible, "http://x/v1", new ProviderConfig { Temperature = 0.4, TopP = 0.8, MaxTokens = 123 });
        Assert.Equal(0.4, (double)set["temperature"]!);
        Assert.Equal(0.8, (double)set["top_p"]!);
        Assert.Equal(123, (int)set["max_tokens"]!);
    }

    [Fact]
    public async Task Extra_body_can_still_override_the_values()
    {
        var b = await Body(ProviderType.OpenAiCompatible, "http://x/v1",
            new ProviderConfig { Temperature = 0.4, ExtraBody = "{\"temperature\": 1.5, \"top_p\": null}", TopP = 0.5 });
        Assert.Equal(1.5, (double)b["temperature"]!);
        Assert.Null(b["top_p"]);
    }

    [Fact]
    public async Task Anthropic_uses_max_tokens_default_and_setting()
    {
        var d = await Body(ProviderType.Anthropic, "http://x", new ProviderConfig());
        Assert.Equal(8192, (int)d["max_tokens"]!);
        var s = await Body(ProviderType.Anthropic, "http://x", new ProviderConfig { MaxTokens = 300, Temperature = 0.1 });
        Assert.Equal(300, (int)s["max_tokens"]!);
        Assert.Equal(0.1, (double)s["temperature"]!);
    }

    [Fact]
    public async Task Gemini_uses_generationConfig()
    {
        var none = await Body(ProviderType.Gemini, "http://x", new ProviderConfig());
        Assert.Null(none["generationConfig"]);
        var s = await Body(ProviderType.Gemini, "http://x", new ProviderConfig { Temperature = 0.6, TopP = 0.7, MaxTokens = 64 });
        var gc = s["generationConfig"]!;
        Assert.Equal(0.6, (double)gc["temperature"]!);
        Assert.Equal(0.7, (double)gc["topP"]!);
        Assert.Equal(64, (int)gc["maxOutputTokens"]!);
    }

    [Fact]
    public async Task Connector_can_use_the_values_as_placeholders()
    {
        var h = new FakeHandler(_ => FakeHandler.Text("data: [DONE]\n\n"));
        var cfg = Cfg("""
        { "kind":"chat", "request": { "path": "/c", "body": { "t": "{{temperature}}", "p": "{{top_p}}", "n": "{{max_tokens}}", "q":"{{prompt}}" } },
          "stream": { "format":"none", "textPath":"$.a" } }
        """);
        cfg.Temperature = 0.5;
        cfg.MaxTokens = 77;
        try { await RunAsync(h, cfg, User("hi")); } catch { /* the fake answers no JSON; only the request matters */ }
        var b = J(h.Requests[0].Body);
        Assert.Equal(0.5, (double)b["t"]!);
        Assert.Equal(77, (int)b["n"]!);
        Assert.False(((JsonObject)b).ContainsKey("p"));
    }
}
