using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using AiChat.Models;
using AiChat.Services;
using Xunit;

namespace AiChat.Tests;

public class LocalServerTests
{
    sealed class FakeHost : ILocalHost
    {
        public List<ProviderConfig> P = new() { new ProviderConfig { Name = "Prov", Models = new() { "m1" }, DefaultModel = "m1" } };
        public IReadOnlyList<ProviderConfig> Providers() => P;

        public async IAsyncEnumerable<string> StreamAsync(ProviderConfig p, string model, IReadOnlyList<ChatMessage> h, string? system, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return "<think>hmm</think>";
            yield return "Hel";
            yield return "lo " + model + "|" + h.Last().Content + "|" + system;
        }

        public IReadOnlyList<McpToolSpec> Tools() => new[]
        {
            new McpToolSpec("echo", "Echo", new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() })
        };

        public Task<string> CallAsync(string name, JsonObject args, CancellationToken ct) =>
            name == "echo" ? Task.FromResult("echo:" + args["x"]) : throw new InvalidOperationException("boom");
    }

    [Fact]
    public void Think_blocks_are_removed_even_when_tags_are_split()
    {
        var s = new ThinkStripper();
        var o = s.Feed("<thi") + s.Feed("nk>secret</th") + s.Feed("ink>\n\nAnswer <b>") + s.Flush();
        Assert.Equal("Answer <b>", o);
        Assert.Equal("", ThinkStripper.StripAll("<think>never closed"));
        Assert.Equal("plain text", ThinkStripper.StripAll("plain text"));
        Assert.Equal("a < b", ThinkStripper.StripAll("a < b"));
    }

    [Fact]
    public void Token_check_is_exact()
    {
        Assert.True(LocalApi.Authorized("Bearer abc", null, "abc"));
        Assert.True(LocalApi.Authorized("bearer abc", null, "abc"));
        Assert.True(LocalApi.Authorized(null, "abc", "abc"));
        Assert.False(LocalApi.Authorized("Bearer abd", null, "abc"));
        Assert.False(LocalApi.Authorized("Bearer abcd", null, "abc"));
        Assert.False(LocalApi.Authorized(null, null, "abc"));
        Assert.False(LocalApi.Authorized("Bearer ", null, "abc"));
        Assert.False(LocalApi.Authorized("Bearer x", null, ""));
        Assert.NotEqual(LocalApi.NewToken(), LocalApi.NewToken());
        Assert.StartsWith("aichat-", LocalApi.NewToken());
    }

    [Fact]
    public void Only_local_hosts_are_served()
    {
        Assert.True(LocalApi.HostOk("localhost:8765"));
        Assert.True(LocalApi.HostOk("127.0.0.1"));
        Assert.True(LocalApi.HostOk("127.0.0.1:1234"));
        Assert.True(LocalApi.HostOk("[::1]:80"));
        Assert.False(LocalApi.HostOk("evil.com"));
        Assert.False(LocalApi.HostOk("127.0.0.1.evil.com"));
        Assert.False(LocalApi.HostOk(null));
        Assert.False(LocalApi.HostOk(""));
    }

    [Fact]
    public void Chat_request_is_parsed()
    {
        var body = JsonNode.Parse("""
        {"model":"x","stream":true,"messages":[
          {"role":"system","content":"Be brief"},
          {"role":"developer","content":"Use Persian"},
          {"role":"user","content":[{"type":"text","text":"Hello"},{"type":"image_url","image_url":{"url":"data:..."}}]},
          {"role":"assistant","content":"Hi"},
          {"role":"tool","content":"42"},
          {"role":"user","content":"Thanks"}]}
        """)!.AsObject();
        var c = LocalApi.Parse(body);
        Assert.Null(c.Error);
        Assert.True(c.Stream);
        Assert.Equal("x", c.Model);
        Assert.Equal("Be brief\n\nUse Persian", c.System);
        Assert.Equal(4, c.Messages.Count);
        Assert.Contains("Hello", c.Messages[0].Content);
        Assert.Contains("[image omitted]", c.Messages[0].Content);
        Assert.Equal("assistant", c.Messages[1].Role);
        Assert.Contains("42", c.Messages[2].Content);

        Assert.NotNull(LocalApi.Parse(JsonNode.Parse("{}")!.AsObject()).Error);
        Assert.NotNull(LocalApi.Parse(JsonNode.Parse("{\"messages\":[{\"role\":\"system\",\"content\":\"x\"}]}")!.AsObject()).Error);
    }

    static List<ProviderConfig> Two() => new()
    {
        new ProviderConfig { Name = "OpenAI", Models = new() { "gpt-4o", "gpt-mini" }, DefaultModel = "gpt-4o" },
        new ProviderConfig { Name = "Local", Models = new() { "llama3" } },
        new ProviderConfig { Name = "Img", Type = ProviderType.FooocusApi, Models = new() { "sdxl" } }
    };

    [Fact]
    public void Models_are_resolved()
    {
        var all = Two();
        var (p1, m1, e1) = LocalApi.Resolve(all, "OpenAI/gpt-mini", "");
        Assert.Equal(("OpenAI", "gpt-mini"), (p1!.Name, m1));
        Assert.Null(e1);
        var (p2, m2, _) = LocalApi.Resolve(all, "llama3", "");
        Assert.Equal(("Local", "llama3"), (p2!.Name, m2));
        var (p3, m3, _) = LocalApi.Resolve(all, "", "");
        Assert.Equal(("OpenAI", "gpt-4o"), (p3!.Name, m3));
        var (p4, m4, _) = LocalApi.Resolve(all, "aichat", "Local/llama3");
        Assert.Equal(("Local", "llama3"), (p4!.Name, m4));
        Assert.NotNull(LocalApi.Resolve(all, "nope", "").Error);
        Assert.NotNull(LocalApi.Resolve(all, "Img/sdxl", "").Error);   // image generators are not chat models
        Assert.NotNull(LocalApi.Resolve(new List<ProviderConfig>(), "x", "").Error);
        var single = new List<ProviderConfig> { all[0] };
        Assert.Equal("anything", LocalApi.Resolve(single, "anything", "").Model);
        Assert.Equal(new[] { "OpenAI/gpt-4o", "OpenAI/gpt-mini", "Local/llama3" }, LocalApi.ModelIds(all).ToArray());
    }

    [Fact]
    public void Json_bodies_have_the_openai_shape()
    {
        var c = LocalApi.Completion("id1", "M", "hi");
        Assert.Equal("chat.completion", (string?)c["object"]);
        Assert.Equal("hi", (string?)c["choices"]![0]!["message"]!["content"]);
        var ch = LocalApi.Chunk("id1", "M", "x");
        Assert.Equal("chat.completion.chunk", (string?)ch["object"]);
        Assert.Equal("x", (string?)ch["choices"]![0]!["delta"]!["content"]);
        var end = LocalApi.Chunk("id1", "M", null, "stop");
        Assert.Equal("stop", (string?)end["choices"]![0]!["finish_reason"]);
        Assert.StartsWith("data: {", LocalApi.Sse(ch));
        Assert.EndsWith("\n\n", LocalApi.Sse(ch));
        Assert.Equal("a/b", (string?)LocalApi.ModelsList(new[] { "a/b" })["data"]![0]!["id"]);
    }

    [Fact]
    public async Task Mcp_methods_work()
    {
        var host = new FakeHost();
        var init = await LocalMcp.HandleAsync(JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\"}}"), host, default);
        Assert.Equal("2025-03-26", (string?)init!["result"]!["protocolVersion"]);
        Assert.Equal("aichat", (string?)init["result"]!["serverInfo"]!["name"]);
        Assert.Equal(1, (int?)init["id"]);

        var other = await LocalMcp.HandleAsync(JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"1999-01-01\"}}"), host, default);
        Assert.Equal("2025-06-18", (string?)other!["result"]!["protocolVersion"]);

        Assert.Null(await LocalMcp.HandleAsync(JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"), host, default));

        var list = await LocalMcp.HandleAsync(JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/list\"}"), host, default);
        Assert.Equal("echo", (string?)list!["result"]!["tools"]![0]!["name"]);

        var call = await LocalMcp.HandleAsync(JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"echo\",\"arguments\":{\"x\":\"hey\"}}}"), host, default);
        Assert.Equal("echo:hey", (string?)call!["result"]!["content"]![0]!["text"]);
        Assert.False((bool?)call["result"]!["isError"]);

        var bad = await LocalMcp.HandleAsync(JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"nope\"}}"), host, default);
        Assert.Equal(-32602, (int?)bad!["error"]!["code"]);

        var unk = await LocalMcp.HandleAsync(JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"zzz\"}"), host, default);
        Assert.Equal(-32601, (int?)unk!["error"]!["code"]);

        var ping = await LocalMcp.HandleAsync(JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"ping\"}"), host, default);
        Assert.NotNull(ping!["result"]);

        var batch = await LocalMcp.HandleAsync(JsonNode.Parse("[{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"ping\"},{\"jsonrpc\":\"2.0\",\"method\":\"notifications/x\"}]"), host, default);
        Assert.Single(batch!.AsArray());
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    [Fact]
    public async Task Server_end_to_end()
    {
        var o = new LocalServerOptions { Enabled = true, Port = FreePort(), Token = "tok", LogRequests = false };
        using var server = new LocalHttpServer(o, new FakeHost(), _ => { });
        Assert.Null(server.Start());
        var url = $"http://127.0.0.1:{o.Port}";
        using var http = new HttpClient();

        // health needs no token and gives nothing private
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(url + "/health")).StatusCode);

        // everything else needs the token
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(url + "/v1/models")).StatusCode);
        var wrong = new HttpRequestMessage(HttpMethod.Get, url + "/v1/models");
        wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "bad");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(wrong)).StatusCode);

        HttpRequestMessage Req(HttpMethod m, string path, string? body = null)
        {
            var r = new HttpRequestMessage(m, url + path);
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "tok");
            if (body != null) r.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return r;
        }

        var models = await (await http.SendAsync(Req(HttpMethod.Get, "/v1/models"))).Content.ReadAsStringAsync();
        Assert.Contains("Prov/m1", models);

        // a browser page cannot call it (it sends Origin)
        var browser = Req(HttpMethod.Get, "/v1/models");
        browser.Headers.Add("Origin", "http://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(browser)).StatusCode);

        // normal answer
        const string reqBody = "{\"model\":\"Prov/m1\",\"messages\":[{\"role\":\"system\",\"content\":\"sys\"},{\"role\":\"user\",\"content\":\"hi\"}]}";
        var resp = await http.SendAsync(Req(HttpMethod.Post, "/v1/chat/completions", reqBody));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        Assert.Equal("Hello m1|hi|sys", (string?)json["choices"]![0]!["message"]!["content"]);

        // streamed answer
        var sresp = await http.SendAsync(Req(HttpMethod.Post, "/v1/chat/completions", reqBody.Replace("\"model\"", "\"stream\":true,\"model\"")));
        var text = await sresp.Content.ReadAsStringAsync();
        Assert.Contains("data: [DONE]", text);
        var sb = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            if (!line.StartsWith("data: {")) continue;
            var d = JsonNode.Parse(line[6..])!["choices"]![0]!["delta"]!["content"];
            if (d != null) sb.Append((string?)d);
        }
        Assert.Equal("Hello m1|hi|sys", sb.ToString());

        // unknown model / bad body
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Req(HttpMethod.Post, "/v1/chat/completions", reqBody.Replace("Prov/m1", "other")))).StatusCode); // single API: any name passes
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(Req(HttpMethod.Post, "/v1/chat/completions", "{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(Req(HttpMethod.Post, "/v1/chat/completions", "not json"))).StatusCode);

        // MCP
        var mcp = await http.SendAsync(Req(HttpMethod.Post, "/mcp", "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}"));
        Assert.Contains("echo", await mcp.Content.ReadAsStringAsync());
        var note = await http.SendAsync(Req(HttpMethod.Post, "/mcp", "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"));
        Assert.Equal(HttpStatusCode.Accepted, note.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.SendAsync(Req(HttpMethod.Get, "/mcp"))).StatusCode);

        // switched off parts answer 404
        o.Mcp = false;
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(Req(HttpMethod.Post, "/mcp", "{}"))).StatusCode);
    }
}
