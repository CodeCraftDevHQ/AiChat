using System.Text;
using AiChat.Models;
using AiChat.Services;
using Xunit;
using static AiChat.Tests.Helpers;

namespace AiChat.Tests;

public class ConnectorFileTests
{
    static readonly byte[] Mp4 = { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x6D, 0x70, 0x34, 0x32, 1, 2, 3, 4 };

    static Attachment Att(string name, string kind, string mime, byte[] bytes)
    {
        var a = new Attachment { Name = name, Kind = kind, MimeType = mime, Size = bytes.Length };
        StorageService.Files[a.Id] = bytes;
        return a;
    }

    const string Viggle = """
    { "kind": "file", "auth": { "type": "bearer" },
      "request": { "method": "POST", "path": "/motions", "bodyType": "multipart",
        "body": {
          "motion_video": { "file": "{{video1_base64}}", "mime": "{{video1_mime}}", "filename": "{{video1_name}}" },
          "motion_video_url": "{{prompt|http}}",
          "type": "glb" } },
      "response": { "type": "json", "text": "$.id" } }
    """;

    [Fact]
    public async Task Video_is_sent_as_a_multipart_file_even_without_text()
    {
        var h = new FakeHandler(_ => FakeHandler.Json("{\"id\":\"mot_1\"}"));
        var run = await RunAsync(h, Cfg(Viggle), User("", Att("walk.mp4", "video", "video/mp4", Mp4)));
        var r = h.Requests[0];
        Assert.StartsWith("multipart/form-data", r.ContentType);
        Assert.Contains("name=motion_video", r.Body.Replace("\"", ""));
        Assert.Contains("walk.mp4", r.Body);
        Assert.Contains("video/mp4", r.Body);
        Assert.DoesNotContain("motion_video_url", r.Body);
        Assert.Contains("glb", r.Body);
        Assert.Contains("mot_1", string.Join(" ", run.Texts));
    }

    [Fact]
    public async Task Prompt_http_filter_sends_links_only()
    {
        var h = new FakeHandler(_ => FakeHandler.Json("{\"id\":\"m\"}"));
        await RunAsync(h, Cfg(Viggle), User("https://cdn.test/a.mp4"));
        var body = h.Requests[0].Body;
        Assert.Contains("motion_video_url", body);
        Assert.Contains("https://cdn.test/a.mp4", body);
        Assert.DoesNotContain("name=motion_video;", body.Replace("\"", ""));

        var h2 = new FakeHandler(_ => FakeHandler.Json("{\"id\":\"m\"}"));
        await RunAsync(h2, Cfg(Viggle), User("please convert this", Att("a.mp4", "video", "video/mp4", Mp4)));
        Assert.DoesNotContain("motion_video_url", h2.Requests[0].Body);
    }

    [Fact]
    public async Task No_text_and_no_file_still_asks_for_a_message()
    {
        var h = new FakeHandler(_ => FakeHandler.Json("{\"id\":\"m\"}"));
        var cfg = Cfg("""{ "request": { "path": "/x", "body": { "p": "{{prompt}}" } }, "response": { "type": "json", "text": "$.id" } }""");
        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(h, cfg, User("")));
    }

    [Fact]
    public async Task File_placeholders_text_name_mime_and_dataurl()
    {
        var h = new FakeHandler(_ => FakeHandler.Json("{\"id\":\"m\"}"));
        var cfg = Cfg("""
        { "request": { "path": "/j", "body": { "t": "{{file1_text}}", "n": "{{file1_name}}", "m": "{{file1_mime}}", "d": "{{file2_dataurl}}", "none": "{{file3_name}}", "v": "{{video_name}}" } },
          "allowEmptyPrompt": true, "response": { "type": "json", "text": "$.id" } }
        """);
        var json = Att("data.json", "text", "text/plain", Encoding.UTF8.GetBytes("{\"a\":1}"));
        await RunAsync(h, cfg, User("", json, Att("clip.mp4", "video", "video/mp4", Mp4)));
        var b = System.Text.Json.Nodes.JsonNode.Parse(h.Requests[0].Body)!;
        Assert.Equal("{\"a\":1}", (string)b["t"]!);
        Assert.Equal("data.json", (string)b["n"]!);
        Assert.Equal("text/plain", (string)b["m"]!);
        Assert.StartsWith("data:video/mp4;base64,", (string)b["d"]!);
        Assert.Null(b["none"]);
        Assert.Equal("clip.mp4", (string)b["v"]!);
    }

    [Fact]
    public async Task Result_file_gets_its_extension_from_the_rule_or_the_address()
    {
        var h = new FakeHandler(r => r.Url.Contains("files.test")
            ? FakeHandler.Bytes(new byte[] { 1, 2, 3, 4 }, "application/octet-stream")
            : FakeHandler.Json("{\"a\":\"https://files.test/x/rig.fbx?sig=1\",\"b\":\"https://files.test/y/other\"}"));
        var cfg = Cfg("""
        { "inputs": [ { "id": "format", "label": "f", "type": "select", "options": ["fbx","glb"], "default": "glb" } ],
          "request": { "path": "/e", "method": "GET" }, "allowEmptyPrompt": true,
          "response": { "type": "json", "results": [ { "from": "$.a", "as": "url", "mime": "application/octet-stream" }, { "from": "$.b", "as": "url", "mime": "application/octet-stream", "ext": "{{input.format}}" } ] } }
        """);
        var run = await RunAsync(h, cfg, User("go"));
        Assert.Equal(2, run.Images.Count);
        Assert.Contains("ext=fbx", run.Images[0].Mime);
        Assert.Contains("ext=glb", run.Images[1].Mime);
    }

    [Fact]
    public void Viggle_template_is_valid_and_uses_the_video_file()
    {
        var t = ConnectorTemplates.All.Single(x => x.Name.StartsWith("Viggle"));
        Assert.Null(ConnectorSpec.Validate(t.Json));
        Assert.Contains("{{video1_base64}}", t.Json);
        Assert.Contains("\"/motions\"", t.Json);
    }
}
