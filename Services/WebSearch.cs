using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AiChat.Services;

public record SearchResult(string Title, string Url, string Snippet);

/// <summary>
/// Web search for any chat model: the results are put into the system prompt (no tool-calling needed),
/// so it works with every provider. Engines: DuckDuckGo (no key), Brave Search (key), SearXNG (your own server).
/// </summary>
public class WebSearchService
{
    public const string Ddg = "ddg", Brave = "brave", Searx = "searxng";

    readonly HttpClient _http;
    public WebSearchService(HttpClient http) => _http = http;

    public async Task<List<SearchResult>> SearchAsync(string engine, string query, string key, string baseUrl, int max, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length > 300) query = query[..300];
        var q = Uri.EscapeDataString(query);

        HttpRequestMessage req;
        switch (engine)
        {
            case Brave:
                if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Brave Search needs an API key.");
                req = new HttpRequestMessage(HttpMethod.Get, $"https://api.search.brave.com/res/v1/web/search?q={q}&count={max}");
                req.Headers.TryAddWithoutValidation("X-Subscription-Token", key.Trim());
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                break;
            case Searx:
                if (string.IsNullOrWhiteSpace(baseUrl)) throw new InvalidOperationException("Enter the address of your SearXNG server.");
                req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.Trim().TrimEnd('/')}/search?q={q}&format=json");
                break;
            default:
                req = new HttpRequestMessage(HttpMethod.Get, $"https://html.duckduckgo.com/html/?q={q}");
                req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
                req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.8");
                break;
        }

        using (req)
        {
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {Clean(body, 200)}");
            return engine switch
            {
                Brave => ParseBrave(body, max),
                Searx => ParseSearx(body, max),
                _ => ParseDuckDuckGo(body, max)
            };
        }
    }

    // ---------------------------------------------------------------- parsing

    static string Clean(string s, int maxLen = 400)
    {
        s = WebUtility.HtmlDecode(Regex.Replace(s ?? "", "<[^>]+>", " "));
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length > maxLen ? s[..maxLen] + "…" : s;
    }

    public static List<SearchResult> ParseBrave(string json, int max)
    {
        var list = new List<SearchResult>();
        if (JsonNode.Parse(json)?["web"]?["results"] is not JsonArray arr) return list;
        foreach (var r in arr.OfType<JsonObject>())
        {
            var url = JPath.Text(r["url"]);
            if (url.Length == 0) continue;
            list.Add(new SearchResult(Clean(JPath.Text(r["title"]), 200), url, Clean(JPath.Text(r["description"]))));
            if (list.Count >= max) break;
        }
        return list;
    }

    public static List<SearchResult> ParseSearx(string json, int max)
    {
        var list = new List<SearchResult>();
        if (JsonNode.Parse(json)?["results"] is not JsonArray arr) return list;
        foreach (var r in arr.OfType<JsonObject>())
        {
            var url = JPath.Text(r["url"]);
            if (url.Length == 0) continue;
            list.Add(new SearchResult(Clean(JPath.Text(r["title"]), 200), url, Clean(JPath.Text(r["content"]))));
            if (list.Count >= max) break;
        }
        return list;
    }

    static readonly Regex DdgLink = new(@"<a\b[^>]*\bclass=""[^""]*\bresult__a\b[^""]*""[^>]*>(.*?)</a>", RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex DdgSnippet = new(@"<a\b[^>]*\bclass=""[^""]*\bresult__snippet\b[^""]*""[^>]*>(.*?)</a>", RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex Href = new(@"\bhref=""([^""]+)""", RegexOptions.Compiled);

    public static List<SearchResult> ParseDuckDuckGo(string html, int max)
    {
        var list = new List<SearchResult>();
        var links = DdgLink.Matches(html);
        for (var i = 0; i < links.Count && list.Count < max; i++)
        {
            var m = links[i];
            var href = Href.Match(m.Value) is { Success: true } h ? WebUtility.HtmlDecode(h.Groups[1].Value) : "";
            var url = RealUrl(href);
            if (url.Length == 0) continue;

            // the snippet that follows this link, before the next one
            var end = i + 1 < links.Count ? links[i + 1].Index : html.Length;
            var sn = DdgSnippet.Match(html, m.Index + m.Length, Math.Max(0, end - m.Index - m.Length));
            list.Add(new SearchResult(Clean(m.Groups[1].Value, 200), url, sn.Success ? Clean(sn.Groups[1].Value) : ""));
        }
        return list;
    }

    /// <summary>DuckDuckGo wraps results as //duckduckgo.com/l/?uddg=ENCODED_URL&amp;rut=...</summary>
    static string RealUrl(string href)
    {
        if (href.Length == 0) return "";
        if (href.StartsWith("//")) href = "https:" + href;
        var i = href.IndexOf("uddg=", StringComparison.Ordinal);
        if (i >= 0)
        {
            var v = href[(i + 5)..];
            var amp = v.IndexOf('&');
            if (amp >= 0) v = v[..amp];
            return Uri.UnescapeDataString(v);
        }
        return href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : "";
    }

    // ---------------------------------------------------------------- prompt / sources

    /// <summary>Text added to the system prompt.</summary>
    public static string BuildContext(IReadOnlyList<SearchResult> results, DateTime today)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Web search results for the user's latest message (today is {today:yyyy-MM-dd}). " +
                      "Use them when they help, prefer them over your own memory for recent facts, and cite sources like [1] or [2]. " +
                      "If the results do not answer the question, say so instead of guessing.");
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            sb.AppendLine().AppendLine($"[{i + 1}] {r.Title}").AppendLine(r.Url);
            if (r.Snippet.Length > 0) sb.AppendLine(r.Snippet);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Markdown list appended to the answer.</summary>
    public static string SourcesMarkdown(IReadOnlyList<SearchResult> results, string heading)
    {
        var sb = new StringBuilder("\n\n---\n**").Append(heading).AppendLine("**");
        for (var i = 0; i < results.Count; i++)
        {
            var title = results[i].Title.Replace("[", "(").Replace("]", ")");
            sb.AppendLine($"{i + 1}. [{(title.Length > 0 ? title : results[i].Url)}]({results[i].Url})");
        }
        return sb.ToString().TrimEnd();
    }
}
