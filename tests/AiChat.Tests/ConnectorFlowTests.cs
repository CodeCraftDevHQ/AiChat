using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AiChat.Services;
using static AiChat.Tests.Helpers;

namespace AiChat.Tests;

public class ConnectorFlowTests
{
    // ---------------------------------------------------------------- request building

    [Fact]
    public async Task Json_body_gets_prompt_model_and_inputs()
    {
        var h = new FakeHandler(_ => FakeHandler.Bytes(Png, "image/png"));
        var cfg = Cfg("""
        { "request": { "path": "/gen", "body": { "text": "{{prompt}}", "model": "{{model}}", "steps": "{{input.steps}}", "neg": "{{input.neg}}" } },
          "response": { "type": "binary" },
          "inputs": [ {"id":"steps","type":"number","default":"20"}, {"id":"neg"} ] }
        """, inputs: new() { ["steps"] = "30" });
        await RunAsync(h, cfg, User("a cat"), "sdxl");

        var r = Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal("http://api.test/gen", r.Url);
        var body = J(r.Body);
        Assert.Equal("a cat", (string)body["text"]!);
        Assert.Equal("sdxl", (string)body["model"]!);
        Assert.Equal(30, (int)body["steps"]!);
        Assert.False(((JsonObject)body).ContainsKey("neg"));   // empty input -> field dropped
    }

    [Fact]
    public async Task Query_get_and_url_filter()
    {
        var h = new FakeHandler(_ => FakeHandler.Bytes(Png, "image/png"));
        var cfg = Cfg("""
        { "request": { "method": "GET", "path": "/p/{{prompt|url}}", "query": { "w": "{{input.w}}", "skip": "{{input.none}}" } },
          "response": { "type": "binary" }, "inputs": [ {"id":"w","default":"512"} ] }
        """);
        await RunAsync(h, cfg, User("red car"));
        Assert.Equal("http://api.test/p/red%20car?w=512", h.Requests[0].Url);
        Assert.Equal(HttpMethod.Get, h.Requests[0].Method);
    }

    [Fact]
    public async Task Absolute_url_path_is_used_as_is()
    {
        var h = new FakeHandler(_ => FakeHandler.Bytes(Png, "image/png"));
        var cfg = Cfg("""{ "request": { "path": "https://other.example/api", "body": {"p":"{{prompt}}"} }, "response": {"type":"binary"} }""");
        await RunAsync(h, cfg, User("x"));
        Assert.Equal("https://other.example/api", h.Requests[0].Url);
    }

    [Theory]
    [InlineData("bearer", "", "Authorization", "Bearer K1")]
    [InlineData("header", "X-API-KEY", "X-API-KEY", "K1")]
    public async Task Auth_is_applied(string type, string name, string header, string expected)
    {
        var h = new FakeHandler(_ => FakeHandler.Bytes(Png, "image/png"));
        var cfg = Cfg("""{ "auth": {"type":"@T","name":"@N"}, "request": { "path": "/g", "body": {"p":"{{prompt}}"} }, "response": {"type":"binary"} }""".Replace("@T", type).Replace("@N", name), key: "K1");
        await RunAsync(h, cfg, User("x"));
        Assert.Equal(expected, h.Requests[0].Headers[header]);
    }

    [Fact]
    public async Task Query_auth_appends_key()
    {
        var h = new FakeHandler(_ => FakeHandler.Bytes(Png, "image/png"));
        var cfg = Cfg("""{ "auth": {"type":"query","name":"key"}, "request": { "path": "/g?a=1", "body": {"p":"{{prompt}}"} }, "response": {"type":"binary"} }""", key: "K 1");
        await RunAsync(h, cfg, User("x"));
        Assert.Equal("http://api.test/g?a=1&key=K%201", h.Requests[0].Url);
    }

    [Fact]
    public async Task Auth_is_skipped_when_user_overrides_auth_mode()
    {
        var h = new FakeHandler(_ => FakeHandler.Bytes(Png, "image/png"));
        var cfg = Cfg("""{ "auth": {"type":"bearer"}, "request": { "path": "/g", "body": {"p":"{{prompt}}"} }, "response": {"type":"binary"} }""", key: "K1");
        cfg.AuthMode = "none";
        await RunAsync(h, cfg, User("x"));
        Assert.False(h.Requests[0].Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task Form_body()
    {
        var h = new FakeHandler(_ => FakeHandler.Bytes(Png, "image/png"));
        var cfg = Cfg("""{ "request": { "path": "/f", "bodyType": "form", "body": {"prompt":"{{prompt}}","n":2} }, "response": {"type":"binary"} }""");
        await RunAsync(h, cfg, User("a b"));
        Assert.Contains("application/x-www-form-urlencoded", h.Requests[0].ContentType);
        Assert.Contains("prompt=a+b", h.Requests[0].Body);
        Assert.Contains("n=2", h.Requests[0].Body);
    }

    [Fact]
    public async Task Multipart_with_image_file_and_text_field()
    {
        var h = new FakeHandler(_ => FakeHandler.Bytes(Png, "image/png"));
        var cfg = Cfg("""
        { "request": { "path": "/m", "bodyType": "multipart",
            "body": { "prompt": "{{prompt}}", "image": { "file": "{{image_base64}}", "mime": "{{image_mime}}", "filename": "in.png" } } },
          "response": {"type":"binary"} }
        """);
        await RunAsync(h, cfg, User("edit", Image(Png)));
        var r = h.Requests[0];
        Assert.StartsWith("multipart/form-data", r.ContentType);
        Assert.Contains("prompt", r.Body);
        Assert.Contains("in.png", r.Body);
    }

    [Fact]
    public async Task Multipart_file_part_is_skipped_without_attachment()
    {
        var h = new FakeHandler(_ => FakeHandler.Bytes(Png, "image/png"));
        var cfg = Cfg("""
        { "request": { "path": "/m", "bodyType": "multipart",
            "body": { "prompt": "{{prompt}}", "image": { "file": "{{image_base64}}" } } },
          "response": {"type":"binary"} }
        """);
        await RunAsync(h, cfg, User("no image"));
        Assert.DoesNotContain("image", h.Requests[0].Body.Replace("Content-Type: image", ""));
    }

    [Fact]
    public async Task Image_placeholders_second_image_and_array()
    {
        var h = new FakeHandler(_ => FakeHandler.Bytes(Png, "image/png"));
        var cfg = Cfg("""
        { "request": { "path": "/i", "body": { "a": "{{image_dataurl}}", "b": "{{image2_base64}}", "all": "{{images_base64}}" } },
          "allowEmptyPrompt": true, "response": {"type":"binary"} }
        """);
        var two = Image(Png);
        var three = Image(new byte[] { 0xFF, 0xD8, 0xFF, 9 });
        await RunAsync(h, cfg, User("", two, three));
        var b = J(h.Requests[0].Body);
        Assert.StartsWith("data:image/png;base64,", (string)b["a"]!);
        Assert.Equal(Convert.ToBase64String(new byte[] { 0xFF, 0xD8, 0xFF, 9 }), (string)b["b"]!);
        Assert.Equal(2, b["all"]!.AsArray().Count);
    }

    [Fact]
    public async Task Empty_prompt_is_refused_unless_allowed()
    {
        var h = new FakeHandler(_ => FakeHandler.Bytes(Png, "image/png"));
        var cfg = Cfg("""{ "request": { "path": "/g", "body": {"p":"{{prompt}}"} }, "response": {"type":"binary"} }""");
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(h, cfg, User("  ")));
        Assert.Empty(h.Requests);
    }

    // ---------------------------------------------------------------- responses

    [Fact]
    public async Task Binary_response_is_emitted_with_sniffed_mime()
    {
        var h = new FakeHandler(_ => FakeHandler.Bytes(Png, "application/octet-stream"));
        var cfg = Cfg("""{ "request": { "path": "/g", "body": {"p":"{{prompt}}"} }, "response": {"type":"binary"} }""");
        var run = await RunAsync(h, cfg, User("x"));
        var img = Assert.Single(run.Images);
        Assert.Equal("image/png", img.Mime);
        Assert.Equal(Png, img.Data);
    }

    [Fact]
    public async Task Base64_json_results_with_data_url()
    {
        var b64 = Convert.ToBase64String(Png);
        var h = new FakeHandler(_ => FakeHandler.Json($$"""{"data":[{"b":"{{b64}}"},{"b":"data:image/jpeg;base64,{{b64}}"}]}"""));
        var cfg = Cfg("""{ "request": { "path": "/g", "body": {"p":"{{prompt}}"} }, "response": { "results": [ {"from":"$.data[*].b","as":"base64"} ] } }""");
        var run = await RunAsync(h, cfg, User("x"));
        Assert.Equal(2, run.Images.Count);
        Assert.Equal("image/png", run.Images[0].Mime);
        Assert.Equal("image/jpeg", run.Images[1].Mime);
    }

    [Fact]
    public async Task Url_results_are_downloaded_and_key_only_sent_to_own_host()
    {
        var h = new FakeHandler(r => r.Url.EndsWith("/gen")
            ? FakeHandler.Json("""{"files":["/out/1.png","https://cdn.other/2.png"]}""")
            : FakeHandler.Bytes(Png, "image/png"));
        var cfg = Cfg("""
        { "auth": {"type":"bearer"}, "request": { "path": "/gen", "body": {"p":"{{prompt}}"} },
          "response": { "results": [ {"from":"$.files[*]","as":"url"} ] } }
        """, key: "SECRET");
        var run = await RunAsync(h, cfg, User("x"));
        Assert.Equal(2, run.Images.Count);
        Assert.Equal("http://api.test/out/1.png", h.Requests[1].Url);
        Assert.True(h.Requests[1].Headers.ContainsKey("Authorization"));
        Assert.Equal("https://cdn.other/2.png", h.Requests[2].Url);
        Assert.False(h.Requests[2].Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task UrlTemplate_uses_item_placeholders()
    {
        var h = new FakeHandler(r => r.Url.EndsWith("/gen")
            ? FakeHandler.Json("""{"out":[{"name":"a b.png","dir":"d"}]}""")
            : FakeHandler.Bytes(Png, "image/png"));
        var cfg = Cfg("""
        { "request": { "path": "/gen", "body": {"p":"{{prompt}}"} },
          "response": { "results": [ {"from":"$.out[*]","as":"url","urlTemplate":"/view?f={{item.name|url}}&d={{item.dir}}"} ] } }
        """);
        await RunAsync(h, cfg, User("x"));
        Assert.Equal("http://api.test/view?f=a%20b.png&d=d", h.Requests[1].Url);
    }

    [Fact]
    public async Task Text_results()
    {
        var h = new FakeHandler(_ => FakeHandler.Json("""{"caption":"a dog"}"""));
        var cfg = Cfg("""{ "request": { "path": "/d", "body": {"p":"{{prompt}}"} }, "response": { "text": "$.caption" } }""");
        var run = await RunAsync(h, cfg, User("x"));
        Assert.Equal(new[] { "a dog" }, run.Texts);
    }

    [Fact]
    public async Task No_result_is_an_error_with_snippet()
    {
        var h = new FakeHandler(_ => FakeHandler.Json("""{"unexpected":true}"""));
        var cfg = Cfg("""{ "request": { "path": "/g", "body": {"p":"{{prompt}}"} }, "response": { "results": [ {"from":"$.nothing[*]","as":"base64"} ] } }""");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(h, cfg, User("x")));
        Assert.Contains("unexpected", ex.Message);
    }

    [Fact]
    public async Task Http_error_surfaces_status_and_message()
    {
        var h = new FakeHandler(_ => FakeHandler.Json("""{"error":{"message":"bad key"}}""", HttpStatusCode.Unauthorized));
        var cfg = Cfg("""{ "request": { "path": "/g", "body": {"p":"{{prompt}}"} }, "response": {"type":"binary"} }""");
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => RunAsync(h, cfg, User("x")));
        Assert.Contains("401", ex.Message);
        Assert.Contains("bad key", ex.Message);
    }

    // ---------------------------------------------------------------- async jobs

    [Fact]
    public async Task Job_polls_until_done_and_downloads()
    {
        var polls = 0;
        var h = new FakeHandler(r =>
        {
            if (r.Url.EndsWith("/submit")) return FakeHandler.Json("""{"id":"J7"}""");
            if (r.Url.Contains("/status/"))
                return FakeHandler.Json(++polls < 2 ? """{"state":"running"}""" : """{"state":"DONE","files":["/f/1.png"]}""");
            return FakeHandler.Bytes(Png, "image/png");
        });
        var cfg = Cfg("""
        { "request": { "path": "/submit", "body": {"p":"{{prompt}}"} },
          "job": { "id": "$.id", "poll": { "method":"GET", "path": "/status/{{job}}", "intervalSec": 1, "timeoutSec": 30,
                   "statusPath": "$.state", "done": ["done"], "failed": ["failed"] },
                   "response": { "results": [ {"from":"$.files[*]","as":"url"} ] } } }
        """);
        var run = await RunAsync(h, cfg, User("x"));
        Assert.Single(run.Images);
        Assert.Equal(2, polls);
        Assert.Contains(h.Requests, r => r.Url == "http://api.test/status/J7");
    }

    [Fact]
    public async Task Job_failure_reports_error_text()
    {
        var h = new FakeHandler(r => r.Url.EndsWith("/submit")
            ? FakeHandler.Json("""{"id":"J"}""")
            : FakeHandler.Json("""{"state":"failed","err":"out of memory"}"""));
        var cfg = Cfg("""
        { "request": { "path": "/submit", "body": {"p":"{{prompt}}"} },
          "job": { "id": "$.id", "poll": { "method":"GET", "path": "/s/{{job}}", "intervalSec": 1, "statusPath": "$.state",
                   "done": ["done"], "failed": ["failed"], "errorPath": "$.err" },
                   "response": { "type":"binary" } } }
        """);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(h, cfg, User("x")));
        Assert.Contains("out of memory", ex.Message);
    }

    [Fact]
    public async Task Job_without_status_path_ends_when_results_appear()
    {
        var polls = 0;
        var h = new FakeHandler(r =>
        {
            if (r.Url.EndsWith("/submit")) return FakeHandler.Json("""{"id":"J"}""");
            if (r.Url.Contains("/h/")) return FakeHandler.Json(++polls < 2 ? "{}" : """{"J":{"out":["/o.png"]}}""");
            return FakeHandler.Bytes(Png, "image/png");
        });
        var cfg = Cfg("""
        { "request": { "path": "/submit", "body": {"p":"{{prompt}}"} },
          "job": { "id": "$.id", "poll": { "method":"GET", "path": "/h/{{job}}", "intervalSec": 1 },
                   "response": { "results": [ {"from":"$.*.out[*]","as":"url"} ] } } }
        """);
        var run = await RunAsync(h, cfg, User("x"));
        Assert.Single(run.Images);
    }

    [Fact]
    public async Task Job_reports_status_progress_and_elapsed()
    {
        var polls = 0;
        var h = new FakeHandler(r =>
        {
            if (r.Url.EndsWith("/submit")) return FakeHandler.Json("""{"id":"J"}""");
            if (r.Url.Contains("/s/"))
                return FakeHandler.Json(++polls < 2 ? """{"state":"Running","p":0.5}""" : """{"state":"done","f":["/o.png"]}""");
            return FakeHandler.Bytes(Png, "image/png");
        });
        var cfg = Cfg("""
        { "request": { "path": "/submit", "body": {"p":"{{prompt}}"} },
          "job": { "id": "$.id", "poll": { "method":"GET", "path": "/s/{{job}}", "intervalSec": 1, "statusPath": "$.state",
                   "done": ["done"], "failed": ["failed"], "progressPath": "$.p", "progressMax": 1 },
                   "response": { "results": [ {"from":"$.f[*]","as":"url"} ] } } }
        """);
        var run = await RunAsync(h, cfg, User("x"));
        var s = Assert.Single(run.Statuses);
        Assert.StartsWith("Running · 50% · 0:", s);
        Assert.Single(run.Images);
    }

    [Fact]
    public async Task Stop_sends_the_cancel_request()
    {
        var h = new FakeHandler(r => r.Url.EndsWith("/submit")
            ? FakeHandler.Json("""{"id":"J9"}""")
            : FakeHandler.Json("""{"state":"running"}"""));
        var cfg = Cfg("""
        { "request": { "path": "/submit", "body": {"p":"{{prompt}}"} },
          "job": { "id": "$.id", "cancel": { "method":"POST", "path":"/cancel/{{job}}" },
                   "poll": { "method":"GET", "path": "/s/{{job}}", "intervalSec": 1, "statusPath": "$.state", "done": ["done"] },
                   "response": { "type":"binary" } } }
        """);
        using var cts = new CancellationTokenSource();
        var provider = new ConnectorProvider(new HttpClient(h));
        var task = Task.Run(async () =>
        {
            await foreach (var _ in provider.StreamAsync(cfg, "m", User("x"), null, (m, d) => Task.CompletedTask, cts.Token)) { }
        });
        await Task.Delay(1800);   // submitted and polled at least once
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        await Task.Delay(500);    // the cancel request is sent in the background
        Assert.Contains(h.Requests, r => r.Url == "http://api.test/cancel/J9" && r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Job_id_missing_is_an_error()
    {
        var h = new FakeHandler(_ => FakeHandler.Json("""{"nope":1}"""));
        var cfg = Cfg("""
        { "request": { "path": "/submit", "body": {"p":"{{prompt}}"} },
          "job": { "id": "$.id", "poll": { "path": "/s" }, "response": { "type":"binary" } } }
        """);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(h, cfg, User("x")));
        Assert.Contains("job id", ex.Message);
    }

    // ---------------------------------------------------------------- chat connectors

    const string ChatSse = """
    { "kind": "chat", "request": { "path": "/chat", "body": { "model": "{{model}}", "messages": "{{messages_with_system}}", "stream": true } },
      "stream": { "format": "sse", "textPath": "$.choices[0].delta.content", "reasoningPath": "$.choices[0].delta.reasoning_content", "errorPath": "$.error.message" } }
    """;

    [Fact]
    public async Task Chat_sse_streams_text_and_reasoning()
    {
        var sse = "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"think\"}}]}\n\n" +
                  "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\n" +
                  "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\n\n" +
                  "data: [DONE]\n\n";
        var h = new FakeHandler(_ => FakeHandler.Text(sse));
        var run = await RunAsync(h, Cfg(ChatSse), User("hi"), "m1", "be brief");
        var all = string.Concat(run.Texts);
        Assert.Contains("think", all);
        Assert.EndsWith("Hello", all);
        Assert.True(all.IndexOf("think") < all.IndexOf("Hello"));

        var body = J(h.Requests[0].Body);
        Assert.Equal("m1", (string)body["model"]!);
        var msgs = body["messages"]!.AsArray();
        Assert.Equal("system", (string)msgs[0]!["role"]!);
        Assert.Equal("be brief", (string)msgs[0]!["content"]!);
        Assert.Equal("hi", (string)msgs[1]!["content"]!);
    }

    [Fact]
    public async Task Chat_sse_error_event_throws()
    {
        var h = new FakeHandler(_ => FakeHandler.Text("data: {\"error\":{\"message\":\"quota\"}}\n\n"));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => RunAsync(h, Cfg(ChatSse), User("hi")));
        Assert.Contains("quota", ex.Message);
    }

    [Fact]
    public async Task Chat_ndjson()
    {
        var json = """
        { "kind":"chat", "request": { "path": "/api/chat", "body": { "model":"{{model}}", "messages":"{{messages}}" } },
          "stream": { "format":"ndjson", "textPath":"$.message.content" } }
        """;
        var h = new FakeHandler(_ => FakeHandler.Text("{\"message\":{\"content\":\"A\"}}\n{\"message\":{\"content\":\"B\"}}\n{\"done\":true}\n", "application/x-ndjson"));
        var run = await RunAsync(h, Cfg(json), User("hi"));
        Assert.Equal("AB", string.Concat(run.Texts));
    }

    [Fact]
    public async Task Chat_none_format_reads_whole_response()
    {
        var json = """
        { "kind":"chat", "request": { "path": "/c", "body": { "q":"{{prompt}}" } },
          "stream": { "format":"none", "textPath":"$.answer" } }
        """;
        var h = new FakeHandler(_ => FakeHandler.Json("""{"answer":"42"}"""));
        var run = await RunAsync(h, Cfg(json), User("meaning?"));
        Assert.Equal("42", string.Concat(run.Texts));
    }

    // ---------------------------------------------------------------- model lists

    [Fact]
    public async Task Model_list_static()
    {
        var p = new ConnectorProvider(new HttpClient(new FakeHandler(_ => throw new Exception("no http expected"))));
        var cfg = Cfg("""{ "request": {"path":"/x"}, "response": {"type":"binary"}, "models": { "static": ["a","b",""] } }""");
        Assert.Equal(new[] { "a", "b" }, await p.ListModelsAsync(cfg, CancellationToken.None));
    }

    [Fact]
    public async Task Model_list_from_server()
    {
        var h = new FakeHandler(_ => FakeHandler.Json("""{"models":[{"id":"m1"},{"name":"m2"},{"id":"m1"}]}"""));
        var p = new ConnectorProvider(new HttpClient(h));
        var cfg = Cfg("""{ "request": {"path":"/x"}, "response": {"type":"binary"}, "models": { "method":"GET", "path":"/models", "list":"$.models[*]" } }""");
        Assert.Equal(new[] { "m1", "m2" }, await p.ListModelsAsync(cfg, CancellationToken.None));
        Assert.Equal(HttpMethod.Get, h.Requests[0].Method);
    }

    [Fact]
    public async Task Model_list_without_models_section_is_empty()
    {
        var p = new ConnectorProvider(new HttpClient(new FakeHandler(_ => throw new Exception("no http"))));
        var cfg = Cfg("""{ "request": {"path":"/x"}, "response": {"type":"binary"} }""");
        Assert.Empty(await p.ListModelsAsync(cfg, CancellationToken.None));
    }
}
