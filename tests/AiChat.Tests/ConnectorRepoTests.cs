using AiChat.Services;

namespace AiChat.Tests;

public class ConnectorRepoTests
{
    [Theory]
    [InlineData("https://github.com/u/r/blob/main/c/fooocus.json", "https://raw.githubusercontent.com/u/r/main/c/fooocus.json")]
    [InlineData("  https://raw.githubusercontent.com/u/r/main/x.json ", "https://raw.githubusercontent.com/u/r/main/x.json")]
    [InlineData("https://gist.github.com/u/0123abcd", "https://gist.githubusercontent.com/u/0123abcd/raw")]
    [InlineData("https://example.com/a.json", "https://example.com/a.json")]
    public void Github_page_links_become_raw_links(string input, string expected) =>
        Assert.Equal(expected, ConnectorRepo.NormalizeUrl(input));

    [Fact]
    public void Catalog_object_and_relative_urls()
    {
        var json = """
        { "name": "x", "connectors": [
          { "name": "A", "description": "da", "author": "me", "url": "connectors/a.json" },
          { "name": "B", "url": "https://other.test/b.json" },
          { "description": "no url" },
          { "url": "c.json" }
        ] }
        """;
        var list = ConnectorRepo.ParseCatalog(json, "https://raw.githubusercontent.com/u/r/main/index.json");
        Assert.Equal(3, list.Count);
        Assert.Equal("https://raw.githubusercontent.com/u/r/main/connectors/a.json", list[0].Url);
        Assert.Equal("da", list[0].Description);
        Assert.Equal("https://other.test/b.json", list[1].Url);
        Assert.Equal("https://raw.githubusercontent.com/u/r/main/c.json", list[2].Url);
        Assert.Equal("https://raw.githubusercontent.com/u/r/main/c.json", list[2].Name);   // no name -> the address
    }

    [Fact]
    public void Catalog_can_be_a_plain_array_with_comments()
    {
        var list = ConnectorRepo.ParseCatalog("""[ /* c */ {"name":"A","url":"https://a.test/a.json",} ]""", "https://x.test/index.json");
        Assert.Single(list);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"other": 1}""")]
    public void Bad_catalogs_are_rejected(string json) =>
        Assert.Throws<FormatException>(() => ConnectorRepo.ParseCatalog(json, "https://x.test/i.json"));

    const string Good = """
    {
      "_name": "My API", "_baseUrl": "http://127.0.0.1:9000", "_help": "Does things.",
      "request": { "path": "/gen", "body": { "p": "{{prompt}}" } },
      "job": { "id": "$.id", "poll": { "path": "https://status.example.org/s/{{job}}", "statusPath": "$.s", "done": ["ok"] },
               "response": { "results": [ { "from": "$.f[*]", "as": "url", "urlTemplate": "https://cdn.example.net/{{item}}" } ] } }
    }
    """;

    [Fact]
    public void Inspect_lists_every_server_the_connector_talks_to()
    {
        var info = ConnectorRepo.Inspect(Good, out var err);
        Assert.Null(err);
        Assert.Equal("My API", info!.Name);
        Assert.Equal("http://127.0.0.1:9000", info.BaseUrl);
        Assert.Equal("Does things.", info.Help);
        Assert.Equal(new[] { "127.0.0.1:9000", "cdn.example.net", "status.example.org" }.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), info.Hosts);
    }

    [Fact]
    public void Inspect_rejects_invalid_connectors()
    {
        Assert.Null(ConnectorRepo.Inspect("""{"request": {}}""", out var err));
        Assert.NotNull(err);
        Assert.Null(ConnectorRepo.Inspect("nope", out err));
        Assert.NotNull(err);
    }

    [Fact]
    public async Task Fetch_downloads_text_and_reports_http_errors()
    {
        var h = new FakeHandler(r => r.Url.EndsWith("/missing") ? FakeHandler.Json("{}", System.Net.HttpStatusCode.NotFound) : FakeHandler.Text("\uFEFF{\"a\":1}", "application/json"));
        var http = new HttpClient(h);
        Assert.Equal("{\"a\":1}", await ConnectorRepo.FetchAsync(http, "https://x.test/a.json", default));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => ConnectorRepo.FetchAsync(http, "https://x.test/missing", default));
        Assert.Contains("404", ex.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConnectorRepo.FetchAsync(http, "ftp://x.test/a", default));
    }

    [Fact]
    public async Task Files_over_1MB_are_refused()
    {
        var http = new HttpClient(new FakeHandler(_ => FakeHandler.Text(new string('a', ConnectorRepo.MaxBytes + 10), "application/json")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConnectorRepo.FetchAsync(http, "https://x.test/big.json", default));
    }
}
