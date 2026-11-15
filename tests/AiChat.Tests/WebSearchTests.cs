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
        await s.SearchAsync(WebSearchService.Brave, "a b", "KEY", "", new SearchOptions { Count = 3, SafeSearch = "moderate" }, default);
        await s.SearchAsync(WebSearchService.Searx, "a b", "", "http://sx.local/", new SearchOptions { Count = 3 }, default);
        Assert.Equal("https://api.search.brave.com/res/v1/web/search?q=a%20b&count=3&safesearch=moderate", h.Requests[0].Url);
        Assert.Equal("KEY", h.Requests[0].Headers["X-Subscription-Token"]);
        Assert.Equal("http://sx.local/search?q=a%20b&format=json&safesearch=1", h.Requests[1].Url);
    }

    [Fact]
    public async Task Missing_key_or_server_is_reported()
    {
        var s = new WebSearchService(new HttpClient(new FakeHandler(_ => throw new Exception("no http"))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.SearchAsync(WebSearchService.Brave, "x", "", "", new SearchOptions(), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.SearchAsync(WebSearchService.Searx, "x", "", "", new SearchOptions(), default));
    }

    [Fact]
    public async Task Http_error_is_reported()
    {
        var h = new FakeHandler(_ => FakeHandler.Json("""{"error":"rate"}""", System.Net.HttpStatusCode.TooManyRequests));
        var s = new WebSearchService(new HttpClient(h));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => s.SearchAsync(WebSearchService.Ddg, "x", "", "", new SearchOptions(), default));
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

    [Fact]
    public async Task Region_safe_search_and_time_range_reach_the_request()
    {
        var h = new FakeHandler(_ => FakeHandler.Text("<html></html>"));
        var s = new WebSearchService(new HttpClient(h));
        await s.SearchAsync(WebSearchService.Ddg, "x", "", "", new SearchOptions { Region = "de-de", SafeSearch = "strict", TimeRange = "week" }, default);
        await s.SearchAsync(WebSearchService.Brave, "x", "K", "", new SearchOptions { Region = "DE", SafeSearch = "off", TimeRange = "day", Count = 2 }, default);
        Assert.Equal("https://html.duckduckgo.com/html/?q=x&kp=1&kl=de-de&df=w", h.Requests[0].Url);
        Assert.Contains("country=DE", h.Requests[1].Url);
        Assert.Contains("safesearch=off", h.Requests[1].Url);
        Assert.Contains("freshness=pd", h.Requests[1].Url);
    }

    [Fact]
    public void Count_is_limited_to_1_10()
    {
        Assert.Equal(10, new SearchOptions { Count = 99 }.SafeCount);
        Assert.Equal(1, new SearchOptions { Count = 0 }.SafeCount);
    }

    [Fact]
    public async Task Custom_engine_uses_url_template_headers_and_json_paths()
    {
        var h = new FakeHandler(_ => FakeHandler.Json("""{"data":{"items":[{"name":"A","link":"https://a.test","desc":"da"},{"name":"B","link":"https://b.test","desc":"db"}]}}"""));
        var s = new WebSearchService(new HttpClient(h));
        var opt = new SearchOptions
        {
            Count = 1,
            CustomUrl = "https://api.example/s?q={query}&n={count}&k={key}",
            CustomHeaders = "Authorization: Bearer {key}\nX-Test: 1",
            CustomResults = "$.data.items[*]", CustomTitle = "$.name", CustomLink = "$.link", CustomSnippet = "$.desc"
        };
        var r = await s.SearchAsync(WebSearchService.Custom, "hello world", "KEY", "", opt, default);
        Assert.Equal("https://api.example/s?q=hello%20world&n=1&k=KEY", h.Requests[0].Url);
        Assert.Equal("Bearer KEY", h.Requests[0].Headers["Authorization"]);
        Assert.Equal("1", h.Requests[0].Headers["X-Test"]);
        Assert.Equal(new SearchResult("A", "https://a.test", "da"), Assert.Single(r));
    }

    [Fact]
    public void Prompt_template_placeholders_are_replaced()
    {
        var res = new[] { new SearchResult("T", "https://t.test", "s") };
        var custom = WebSearchService.BuildContext(res, new DateTime(2026, 1, 2), "Today {date}. Answer briefly:\n{results}");
        Assert.StartsWith("Today 2026-01-02. Answer briefly:", custom);
        Assert.Contains("[1] T", custom);
        // a template without {results} still gets the results
        Assert.Contains("https://t.test", WebSearchService.BuildContext(res, DateTime.Today, "Be careful."));
        // empty template -> default text
        Assert.Contains("cite sources", WebSearchService.BuildContext(res, DateTime.Today, ""));
    }
}
