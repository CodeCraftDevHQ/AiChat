using System.Net;
using System.Text.Json.Nodes;
using AiChat.Services;

namespace AiChat.Tests;

public class McpTests
{
    // a tiny fake MCP server: JSON replies for most calls, an SSE reply for tools/call
    static FakeHandler Server(bool sseForCall = true, Action<FakeHandler.Seen>? spy = null) => new(seen =>
    {
        spy?.Invoke(seen);
        var req = JsonNode.Parse(seen.Body)!;
        var method = req["method"]!.ToString();
        var id = req["id"];
        string Wrap(string result) => $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{result}}}";
        switch (method)
        {
            case "initialize":
                var r = FakeHandler.Json(Wrap("{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"serverInfo\":{\"name\":\"Demo\"}}"));
                r.Headers.Add("Mcp-Session-Id", "sess-1");
                return r;
            case "notifications/initialized":
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            case "tools/list":
                var cursor = req["params"]?["cursor"]?.ToString();
                return cursor == null
                    ? FakeHandler.Json(Wrap("{\"tools\":[{\"name\":\"echo\",\"description\":\"Echo text\",\"inputSchema\":{\"$schema\":\"x\",\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}},\"additionalProperties\":false}}],\"nextCursor\":\"p2\"}"))
                    : FakeHandler.Json(Wrap("{\"tools\":[{\"name\":\"add numbers!\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"a\":{\"type\":[\"number\",\"null\"]}}}}]}"));
            case "tools/call":
                var text = req["params"]!["arguments"]!["text"]?.ToString() ?? "";
                var body = Wrap("{\"content\":[{\"type\":\"text\",\"text\":\"you said: " + text + "\"}]}");
                return sseForCall
                    ? FakeHandler.Text("event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\"}\n\nevent: message\ndata: " + body + "\n\n")
                    : FakeHandler.Json(body);
        }
        return FakeHandler.Json("{}", HttpStatusCode.BadRequest);
    });

    static McpServerConfig Cfg() => new() { Name = "My Server", Url = "http://mcp.test/mcp", Headers = "Authorization: Bearer tok\nbad line" };

    [Fact]
    public async Task ListTools_FollowsPages_CleansSchema_SendsSessionAndHeaders()
    {
        var h = Server();
        var client = new McpClient(new HttpClient(h), Cfg());
        var tools = await client.ListToolsAsync(default);

        Assert.Equal(new[] { "echo", "add numbers!" }, tools.Select(t => t.Name));
        Assert.Null(tools[0].Schema["$schema"]);
        Assert.Null(tools[0].Schema["additionalProperties"]);

        var list = h.Requests.Where(r => r.Body.Contains("tools/list")).ToList();
        Assert.Equal(2, list.Count);
        Assert.Equal("sess-1", list[0].Headers["Mcp-Session-Id"]);
        Assert.Equal("Bearer tok", list[0].Headers["Authorization"]);
        Assert.Contains("text/event-stream", list[0].Headers["Accept"]);
        Assert.Equal("2025-06-18", list[0].Headers["MCP-Protocol-Version"]);
        Assert.False(h.Requests[0].Headers.ContainsKey("Mcp-Session-Id"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallTool_ReadsJsonOrSse(bool sse)
    {
        var client = new McpClient(new HttpClient(Server(sse)), Cfg());
        var text = await client.CallToolAsync("echo", new JsonObject { ["text"] = "hi" }, default);
        Assert.Equal("you said: hi", text);
    }

    [Fact]
    public async Task Errors_AreReported()
    {
        var h = new FakeHandler(_ => FakeHandler.Json("{\"error\":\"no\"}", HttpStatusCode.Unauthorized));
        var client = new McpClient(new HttpClient(h), Cfg());
        var ex = await Assert.ThrowsAsync<McpException>(() => client.ListToolsAsync(default));
        Assert.Contains("401", ex.Message);

        var h2 = new FakeHandler(s =>
        {
            var id = JsonNode.Parse(s.Body)!["id"];
            return s.Body.Contains("\"initialize\"")
                ? FakeHandler.Json($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{}}}}")
                : s.Body.Contains("notifications/")
                    ? new HttpResponseMessage(HttpStatusCode.Accepted)
                    : FakeHandler.Json($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"error\":{{\"code\":-32602,\"message\":\"bad args\"}}}}");
        });
        var c2 = new McpClient(new HttpClient(h2), Cfg());
        var ex2 = await Assert.ThrowsAsync<McpException>(() => c2.CallToolAsync("x", new JsonObject(), default));
        Assert.Contains("bad args", ex2.Message);
    }

    [Fact]
    public void FormatResult_JoinsPartsAndMarksErrors()
    {
        var node = JsonNode.Parse("{\"content\":[{\"type\":\"text\",\"text\":\"a\"},{\"type\":\"image\",\"mimeType\":\"image/png\",\"data\":\"..\"},{\"type\":\"text\",\"text\":\"b\"}],\"isError\":true}");
        var s = McpClient.FormatResult(node);
        Assert.StartsWith("Error: a", s);
        Assert.Contains("[image: image/png]", s);
        Assert.EndsWith("b", s);
    }

    [Fact]
    public async Task Builder_NamesAreSafeUnique_AndRunCallsServer()
    {
        var client = new McpClient(new HttpClient(Server()), Cfg());
        var cfg = Cfg();
        var infos = await client.ListToolsAsync(default);
        var used = new HashSet<string>();
        var tools = McpToolBuilder.Build(cfg, infos.Append(infos[0]), client, used);

        Assert.Equal("mcp_My_Server_echo", tools[0].Name);
        Assert.Equal("mcp_My_Server_add_numbers", tools[1].Name);
        Assert.Equal("mcp_My_Server_echo_2", tools[2].Name);
        Assert.All(tools, t => Assert.Matches("^[a-zA-Z0-9_-]{1,64}$", t.Name));
        Assert.All(tools, t => Assert.Equal("mcp", t.Source));

        var set = new ToolSet { Tools = tools };
        Assert.Equal("you said: yo", await set.ExecuteAsync(tools[0].Name, "{\"text\":\"yo\"}", default));

        cfg.DisabledTools.Add("echo");
        Assert.Single(McpToolBuilder.Build(cfg, infos, client, new HashSet<string>()));
        cfg.Ask = true;
        Assert.True(McpToolBuilder.Build(cfg, infos, client, new HashSet<string>())[0].Ask);
    }

    [Fact]
    public void LongNames_AreCutTo64()
    {
        var n = McpToolBuilder.ToolName("server", new string('x', 100), new HashSet<string>());
        Assert.Equal(64, n.Length);
    }

    [Fact]
    public void GeminiSchema_DropsUnsupportedKeys_AndFixesTypeArrays()
    {
        var schema = (JsonObject)JsonNode.Parse("{\"type\":\"object\",\"properties\":{\"a\":{\"type\":[\"string\",\"null\"],\"default\":\"x\",\"pattern\":\"y\"},\"b\":{\"type\":\"array\",\"items\":{\"type\":\"integer\",\"exclusiveMinimum\":0}}},\"required\":[\"a\"],\"additionalProperties\":false}")!;
        var g = McpSchema.ForGemini(schema);
        Assert.Null(g["additionalProperties"]);
        Assert.Equal("string", g["properties"]!["a"]!["type"]!.ToString());
        Assert.True((bool)g["properties"]!["a"]!["nullable"]!);
        Assert.Null(g["properties"]!["a"]!["default"]);
        Assert.Null(g["properties"]!["a"]!["pattern"]);
        Assert.Null(g["properties"]!["b"]!["items"]!["exclusiveMinimum"]);
        Assert.NotNull(schema["additionalProperties"]); // the original is untouched
    }

    [Fact]
    public void ImportJson_ReadsMcpServersFormat_SkipsStdio()
    {
        var json = "{ \"mcpServers\": { \"web\": { \"url\": \"https://x.test/mcp\", \"headers\": { \"Authorization\": \"Bearer t\" } }, \"local\": { \"command\": \"npx\", \"args\": [\"-y\", \"pkg\"] } } }";
        var list = McpToolBuilder.ImportJson(json, out var skipped);
        Assert.Single(list);
        Assert.Equal("web", list[0].Name);
        Assert.Equal("Authorization: Bearer t", list[0].Headers);
        Assert.Equal(new[] { "local" }, skipped);
    }

    [Fact]
    public void Config_Serialization_DoesNotContainHeaders()
    {
        var c = Cfg();
        var json = System.Text.Json.JsonSerializer.Serialize(c);
        Assert.DoesNotContain("tok", json);
        Assert.Contains("mcp.test", json);
    }
}
