using System.Text;
using AiChat.Services;
using static AiChat.Tests.Helpers;

namespace AiChat.Tests;

public class SeedTests
{
    [Fact]
    public void MimeMeta_round_trip()
    {
        var raw = MimeMeta.Add("image/png", "seed", "123");
        Assert.Equal("image/png;seed=123", raw);
        var (mime, meta) = MimeMeta.Split(raw);
        Assert.Equal("image/png", mime);
        Assert.Equal("123", meta["seed"]);
    }

    [Fact]
    public void MimeMeta_without_value_or_params()
    {
        Assert.Equal("image/png", MimeMeta.Add("image/png", "seed", null));
        var (mime, meta) = MimeMeta.Split("image/png");
        Assert.Equal("image/png", mime);
        Assert.Empty(meta);
    }

    [Fact]
    public async Task Seeds_path_pairs_results_with_seeds()
    {
        var b64 = Convert.ToBase64String(Png);
        var json = "{\"items\":[{\"b\":\"" + b64 + "\",\"s\":111},{\"b\":\"" + b64 + "\",\"s\":222}]}";
        var h = new FakeHandler(_ => FakeHandler.Json(json));
        var cfg = Cfg("""
        { "request": { "path": "/g", "body": { "p": "{{prompt}}" } },
          "response": { "seeds": "$.items[*].s", "results": [ { "from": "$.items[*].b", "as": "base64", "mime": "image/png" } ] } }
        """);
        var run = await RunAsync(h, cfg, User("x"));
        Assert.Equal(2, run.Images.Count);
        Assert.Equal("image/png;seed=111", run.Images[0].Mime);
        Assert.Equal("image/png;seed=222", run.Images[1].Mime);
    }

    [Fact]
    public async Task Sent_seed_is_attached_when_the_request_used_it()
    {
        var h = new FakeHandler(_ => FakeHandler.Bytes(Png, "image/png"));
        var cfg = Cfg("""{ "request": { "path": "/g", "body": { "seed": "{{seed}}" } }, "response": { "type": "binary" } }""");
        var run = await RunAsync(h, cfg, User("x"));
        var (_, meta) = MimeMeta.Split(run.Images[0].Mime);
        Assert.True(meta.ContainsKey("seed"));
    }

    [Fact]
    public async Task No_seed_no_metadata()
    {
        var h = new FakeHandler(_ => FakeHandler.Bytes(Png, "image/png"));
        var cfg = Cfg("""{ "request": { "path": "/g", "body": { "p": "{{prompt}}" } }, "response": { "type": "binary" } }""");
        var run = await RunAsync(h, cfg, User("x"));
        Assert.Equal("image/png", run.Images[0].Mime);
    }
}
