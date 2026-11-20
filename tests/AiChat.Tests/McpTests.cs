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
        var list = McpToolBuilder.ImportJson(json, out var skipped, false);
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

/// <summary>A one-way in-memory byte pipe: the reader blocks until data arrives; disposing the writer ends the stream (like a closed program).</summary>
sealed class MemPipe
{
    readonly System.Threading.Channels.Channel<byte[]> _ch = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
    public Stream Reader => new R(this);
    public Stream Writer => new W(this);

    abstract class Base : Stream
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    sealed class R(MemPipe p) : Base
    {
        byte[]? _cur;
        int _pos;
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (_cur == null || _pos >= _cur.Length)
            {
                if (!await p._ch.Reader.WaitToReadAsync(ct)) return 0;
                if (p._ch.Reader.TryRead(out var next)) { _cur = next; _pos = 0; }
            }
            var n = Math.Min(buffer.Length, _cur.Length - _pos);
            _cur.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            p._ch.Writer.TryComplete();
            base.Dispose(disposing);
        }
    }

    sealed class W(MemPipe p) : Base
    {
        public override bool CanRead => false;
        public override bool CanWrite => true;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (!p._ch.Writer.TryWrite(buffer.AsSpan(offset, count).ToArray())) throw new IOException("The pipe is closed.");
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        { Write(buffer, offset, count); return Task.CompletedTask; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (!p._ch.Writer.TryWrite(buffer.ToArray())) throw new IOException("The pipe is closed.");
            return ValueTask.CompletedTask;
        }
        protected override void Dispose(bool disposing)
        {
            p._ch.Writer.TryComplete();
            base.Dispose(disposing);
        }
    }
}

public class McpStdioTests
{
    /// <summary>A fake local program: reads JSON lines from "stdin", answers on "stdout".</summary>
    static McpStdioTransport.Running FakeProgram(Func<string, string?> handler, bool noise = false)
    {
        // in-memory pipes (OS pipes behave differently on Windows and Linux when both ends live in one process)
        var c2s = new MemPipe();
        var s2c = new MemPipe();
        var serverIn = c2s.Reader;
        var serverOut = s2c.Writer;

        _ = Task.Run(() =>
        {
            try
            {
                using var reader = new StreamReader(serverIn);
                using var writer = new StreamWriter(serverOut) { AutoFlush = true, NewLine = "\n" };
                if (noise) writer.WriteLine("Server v1 starting...");
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    var reply = handler(line);
                    if (reply == "<exit>") return;
                    if (reply != null) writer.WriteLine(reply);
                }
            }
            catch { }
        });

        return new McpStdioTransport.Running(c2s.Writer, s2c.Reader, () => "boom: missing module", () =>
        {
            try { serverIn.Dispose(); } catch { }
            try { serverOut.Dispose(); } catch { }
        });
    }

    static string? Echo(string line)
    {
        var req = JsonNode.Parse(line)!;
        var method = req["method"]!.ToString();
        var id = req["id"];
        string Wrap(string r) => $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{r}}}";
        return method switch
        {
            "initialize" => Wrap("{\"protocolVersion\":\"2025-06-18\",\"serverInfo\":{\"name\":\"Local\"}}"),
            "tools/list" => Wrap("{\"tools\":[{\"name\":\"echo\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}}}}]}"),
            "tools/call" => Wrap("{\"content\":[{\"type\":\"text\",\"text\":\"local: " + req["params"]!["arguments"]!["text"] + "\"}]}"),
            _ => null
        };
    }

    [Fact]
    public async Task Stdio_ListAndCall_IgnoresNoiseLines()
    {
        var cfg = new McpServerConfig { Name = "Local", Transport = "stdio", Command = "x", TimeoutSec = 10 };
        using var transport = new McpStdioTransport(() => FakeProgram(Echo, noise: true));
        var client = new McpClient(transport, cfg);

        var tools = await client.ListToolsAsync(default);
        Assert.Equal("echo", Assert.Single(tools).Name);
        Assert.Equal("Local", client.ServerName);

        var text = await client.CallToolAsync("echo", new JsonObject { ["text"] = "hi" }, default);
        Assert.Equal("local: hi", text);
    }

    [Fact]
    public async Task Stdio_ProgramExit_FailsWithStderr()
    {
        var cfg = new McpServerConfig { Name = "Local", Transport = "stdio", Command = "x", TimeoutSec = 10 };
        using var transport = new McpStdioTransport(() => FakeProgram(_ => "<exit>"));
        var client = new McpClient(transport, cfg);
        var ex = await Assert.ThrowsAsync<McpException>(() => client.ListToolsAsync(default));
        Assert.Contains("stopped", ex.Message);
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task Stdio_Timeout_WhenProgramNeverAnswers()
    {
        var cfg = new McpServerConfig { Name = "Local", Transport = "stdio", Command = "x", TimeoutSec = 3 };
        using var transport = new McpStdioTransport(() => FakeProgram(_ => null));
        var client = new McpClient(transport, cfg);
        var ex = await Assert.ThrowsAsync<McpException>(() => client.ListToolsAsync(default));
        Assert.Contains("Timed out", ex.Message);
    }

    [Fact]
    public void CommandLine_QuotesArguments()
    {
        Assert.Equal("npx -y @modelcontextprotocol/server-filesystem \"C:\\My Docs\"",
            McpProcess.BuildCommandLine("npx", new[] { "-y", "@modelcontextprotocol/server-filesystem", "C:\\My Docs" }));
        Assert.Equal("\"\"", McpProcess.BuildCommandLine("", Array.Empty<string>()));
        Assert.Contains("\"a&b\"", McpProcess.BuildCommandLine("x", new[] { "a&b" }));
    }

    [Fact]
    public void Env_IsParsed()
    {
        var env = McpProcess.ParseEnv("A=1\r\nbad\nKEY = va=lue \n");
        Assert.Equal(new[] { ("A", "1"), ("KEY", "va=lue") }, env.ToArray());
    }

    [Fact]
    public void Import_ProgramEntries_AreAddedSwitchedOff()
    {
        var json = "{ \"mcpServers\": { \"fs\": { \"command\": \"npx\", \"args\": [\"-y\", \"pkg\", \"C:/x\"], \"env\": { \"K\": \"v\" } } } }";
        var list = McpToolBuilder.ImportJson(json, out var skipped, true);
        var s = Assert.Single(list);
        Assert.True(s.IsStdio);
        Assert.False(s.Enabled);
        Assert.Equal(new[] { "-y", "pkg", "C:/x" }, s.Args);
        Assert.Equal("K=v", s.Env);
        Assert.Empty(skipped);

        McpToolBuilder.ImportJson(json, out var skipped2, false);
        Assert.Equal(new[] { "fs" }, skipped2);
    }

    [Fact]
    public void Config_Serialization_KeepsStdioFields_ButNotEnv()
    {
        var c = new McpServerConfig { Name = "n", Transport = "stdio", Command = "npx", Args = { "a b" }, Env = "SECRET=1" };
        var json = System.Text.Json.JsonSerializer.Serialize(c);
        Assert.DoesNotContain("SECRET", json);
        var back = System.Text.Json.JsonSerializer.Deserialize<McpServerConfig>(json)!;
        Assert.True(back.IsStdio);
        Assert.Equal(new[] { "a b" }, back.Args);
        Assert.False(new McpServerConfig().IsStdio);
    }
}
