using AiChat.Services;

namespace AiChat.Tests;

public class WebSearchTests
{
    const string DdgHtml = """
    <div class="result"><h2 class="result__title"><a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fa%3Fx%3D1&amp;rut=abc">First <b>result</b></a></h2>
      <a class="result__snippet" href="//duckduckgo.com/l/?uddg=x">Snippet &amp; text of <b>one</b></a></div>
    <div class="result"><h2><a class="result__a" rel="nofollow" href="https://direct.example/b">Second</a></h2>
      <a class="result__snippet" href="x">Snippet two</a></div>
    <div class="result"><h2><a class="result__a" href="https://third.example/">Third</a></h2></div>
    """;

    [Fact]
    public void DuckDuckGo_html_is_parsed()
    {
        var r = WebSearchService.ParseDuckDuckGo(DdgHtml, 10);
        Assert.Equal(3, r.Count);
        Assert.Equal("First result", r[0].Title);
        Assert.Equal("https://example.com/a?x=1", r[0].Url);
        Assert.Equal("Snippet & text of one", r[0].Snippet);
        Assert.Equal("https://direct.example/b", r[1].Url);
        Assert.Equal("Snippet two", r[1].Snippet);
        Assert.Equal("", r[2].Snippet);   // no snippet must not steal the next one
    }

    [Fact]
    public void DuckDuckGo_respects_max_and_empty_page()
    {
        Assert.Equal(2, WebSearchService.ParseDuckDuckGo(DdgHtml, 2).Count);
        Assert.Empty(WebSearchService.ParseDuckDuckGo("<html>captcha</html>", 5));
    }

    [Fact]
    public void Brave_json_is_parsed()
    {
        var r = WebSearchService.ParseBrave("""{"web":{"results":[{"title":"T <strong>1</strong>","url":"https://a.test","description":"D <strong>x</strong>"},{"title":"no url"}]}}""", 5);
        var one = Assert.Single(r);
        Assert.Equal("T 1", one.Title);
        Assert.Equal("D x", one.Snippet);
    }

    [Fact]
    public void Searx_json_is_parsed()
    {
        var r = WebSearchService.ParseSearx("""{"results":[{"title":"A","url":"https://a.test","content":"C"}]}""", 5);
        Assert.Equal(new SearchResult("A", "https://a.test", "C"), Assert.Single(r));
    }

    [Fact]
    public async Task Search_builds_the_right_requests()
    {
        var h = new FakeHandler(_ => FakeHandler.Json("""{"web":{"results":[]},"results":[]}"""));
        var s = new WebSearchService(new HttpClient(h));
        await s.SearchAsync(WebSearchService.Brave, "a b", "KEY", "", 3, default);
        await s.SearchAsync(WebSearchService.Searx, "a b", "", "http://sx.local/", 3, default);
        Assert.Equal("https://api.search.brave.com/res/v1/web/search?q=a%20b&count=3", h.Requests[0].Url);
        Assert.Equal("KEY", h.Requests[0].Headers["X-Subscription-Token"]);
        Assert.Equal("http://sx.local/search?q=a%20b&format=json", h.Requests[1].Url);
    }

    [Fact]
    public async Task Missing_key_or_server_is_reported()
    {
        var s = new WebSearchService(new HttpClient(new FakeHandler(_ => throw new Exception("no http"))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.SearchAsync(WebSearchService.Brave, "x", "", "", 3, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.SearchAsync(WebSearchService.Searx, "x", "", "", 3, default));
    }

    [Fact]
    public async Task Http_error_is_reported()
    {
        var h = new FakeHandler(_ => FakeHandler.Json("""{"error":"rate"}""", System.Net.HttpStatusCode.TooManyRequests));
        var s = new WebSearchService(new HttpClient(h));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => s.SearchAsync(WebSearchService.Ddg, "x", "", "", 3, default));
        Assert.Contains("429", ex.Message);
    }

    [Fact]
    public void Context_and_sources_are_numbered()
    {
        var res = new[] { new SearchResult("One [x]", "https://1.test", "s1"), new SearchResult("Two", "https://2.test", "") };
        var ctx = WebSearchService.BuildContext(res, new DateTime(2026, 11, 15));
        Assert.Contains("2026-11-15", ctx);
        Assert.Contains("[1] One [x]", ctx);
        Assert.Contains("https://2.test", ctx);
        var src = WebSearchService.SourcesMarkdown(res, "Sources");
        Assert.Contains("1. [One (x)](https://1.test)", src);
        Assert.Contains("2. [Two](https://2.test)", src);
    }
}
