using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AiChat.Services;

public record SearchResult(string Title, string Url, string Snippet)
{
    /// <summary>Text of the page itself (only when "read pages" is on).</summary>
    public string Content { get; init; } = "";
}

/// <summary>
/// Web search for any chat model: the results are put into the system prompt (no tool-calling needed),
/// so it works with every provider. Engines: DuckDuckGo (no key), Brave Search (key), SearXNG (your own server).
/// </summary>
/// <summary>User-adjustable search settings (stored as JSON in the preferences).</summary>
public class SearchOptions
{
    public int Count { get; set; } = 5;
    /// <summary>DuckDuckGo: kl (wt-wt, us-en, de-de...), Brave: country (US, DE...), SearXNG: language (en, fa...).</summary>
    public string Region { get; set; } = "";
    /// <summary>off | moderate | strict</summary>
    public string SafeSearch { get; set; } = "moderate";
    /// <summary>"" (any time) | day | week | month | year</summary>
    public string TimeRange { get; set; } = "";
    public int TimeoutSec { get; set; } = 20;
    public bool ShowSources { get; set; } = true;
    /// <summary>last = the last message is the query; ai = the model writes a query from the conversation first.</summary>
    public string QueryMode { get; set; } = "last";
    /// <summary>Instruction given to the model together with the results. Empty = default. Placeholders: {date}, {results}.</summary>
    public string PromptTemplate { get; set; } = "";

    // custom engine
    public string CustomUrl { get; set; } = "";       // e.g. https://api.example.com/search?q={query}&n={count}
    public string CustomHeaders { get; set; } = "";   // one per line: Name: value   ({key} is replaced)
    public string CustomResults { get; set; } = "$.results[*]";
    public string CustomTitle { get; set; } = "$.title";
    public string CustomLink { get; set; } = "$.url";
    public string CustomSnippet { get; set; } = "$.content";

    public int SafeCount => Math.Clamp(Count, 1, 10);

    // reading the pages themselves
    /// <summary>How many of the first results are opened and read (0 = off).</summary>
    public int PageCount { get; set; }
    public int PageChars { get; set; } = 3000;
    public int PageTimeoutSec { get; set; } = 10;
    /// <summary>One domain per line; these sites are never opened (and subdomains of them).</summary>
    public string BlockedDomains { get; set; } = "";
}

public class WebSearchService
{
    public const string Ddg = "ddg", Brave = "brave", Searx = "searxng";

    readonly HttpClient _http;
    public WebSearchService(HttpClient http) => _http = http;

    public const string Custom = "custom";

    public async Task<List<SearchResult>> SearchAsync(string engine, string query, string key, string baseUrl, SearchOptions opt, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length > 300) query = query[..300];
        var q = Uri.EscapeDataString(query);
        var max = opt.SafeCount;
        var region = Uri.EscapeDataString(opt.Region.Trim());

        HttpRequestMessage req;
        switch (engine)
        {
            case Brave:
            {
                if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Brave Search needs an API key.");
                var url = $"https://api.search.brave.com/res/v1/web/search?q={q}&count={max}&safesearch={(opt.SafeSearch is "off" or "strict" ? opt.SafeSearch : "moderate")}";
                if (region.Length > 0) url += "&country=" + region;
                var fresh = opt.TimeRange switch { "day" => "pd", "week" => "pw", "month" => "pm", "year" => "py", _ => "" };
                if (fresh.Length > 0) url += "&freshness=" + fresh;
                req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("X-Subscription-Token", key.Trim());
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                break;
            }
            case Searx:
            {
                if (string.IsNullOrWhiteSpace(baseUrl)) throw new InvalidOperationException("Enter the address of your SearXNG server.");
                var url = $"{baseUrl.Trim().TrimEnd('/')}/search?q={q}&format=json&safesearch={(opt.SafeSearch == "off" ? 0 : opt.SafeSearch == "strict" ? 2 : 1)}";
                if (region.Length > 0) url += "&language=" + region;
                if (opt.TimeRange.Length > 0) url += "&time_range=" + opt.TimeRange;
                req = new HttpRequestMessage(HttpMethod.Get, url);
                break;
            }
            case Custom:
            {
                if (string.IsNullOrWhiteSpace(opt.CustomUrl)) throw new InvalidOperationException("Enter the URL of your custom search engine.");
                string Fill(string t) => t.Replace("{query}", q).Replace("{count}", max.ToString())
                    .Replace("{key}", Uri.EscapeDataString(key.Trim())).Replace("{region}", region);
                req = new HttpRequestMessage(HttpMethod.Get, Fill(opt.CustomUrl.Trim()));
                foreach (var line in opt.CustomHeaders.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var i = line.IndexOf(':');
                    if (i > 0) req.Headers.TryAddWithoutValidation(line[..i].Trim(), line[(i + 1)..].Replace("{key}", key.Trim()).Trim());
                }
                break;
            }
            default:
            {
                var url = $"https://html.duckduckgo.com/html/?q={q}&kp={(opt.SafeSearch == "off" ? "-2" : opt.SafeSearch == "strict" ? "1" : "-1")}";
                if (region.Length > 0) url += "&kl=" + region;
                var df = opt.TimeRange switch { "day" => "d", "week" => "w", "month" => "m", "year" => "y", _ => "" };
                if (df.Length > 0) url += "&df=" + df;
                req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
                req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.8");
                break;
            }
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
                Custom => ParseCustom(body, opt, max),
                _ => ParseDuckDuckGo(body, max)
            };
        }
    }

    public static List<SearchResult> ParseCustom(string json, SearchOptions opt, int max)
    {
        var list = new List<SearchResult>();
        var root = JsonNode.Parse(json);
        if (root == null) return list;
        string Get(JsonNode item, string path) =>
            string.IsNullOrWhiteSpace(path) ? "" : JPath.Text(JPath.Select(item, path).FirstOrDefault());
        foreach (var item in JPath.Select(root, opt.CustomResults))
        {
            var url = Get(item, opt.CustomLink);
            if (url.Length == 0) continue;
            list.Add(new SearchResult(Clean(Get(item, opt.CustomTitle), 200), url, Clean(Get(item, opt.CustomSnippet))));
            if (list.Count >= max) break;
        }
        return list;
    }


    // ---------------------------------------------------------------- reading the pages

    /// <summary>Opens the first <see cref="SearchOptions.PageCount"/> results and adds their text. Failures just leave the snippet.</summary>
    public async Task<List<SearchResult>> ReadPagesAsync(List<SearchResult> results, SearchOptions opt, CancellationToken ct)
    {
        var n = Math.Clamp(opt.PageCount, 0, 10);
        if (n == 0 || results.Count == 0) return results;
        var chars = Math.Clamp(opt.PageChars, 300, 20000);
        var blocked = opt.BlockedDomains.Split(new[] { '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.ToLowerInvariant().TrimStart('*', '.')).Where(d => d.Length > 0).ToList();

        async Task<SearchResult> One(SearchResult r)
        {
            if (!IsAllowed(r.Url, blocked)) return r;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(opt.PageTimeoutSec, 3, 60)));
                using var req = new HttpRequestMessage(HttpMethod.Get, r.Url);
                req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
                req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (!resp.IsSuccessStatusCode) return r;
                var type = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (type.Length > 0 && !type.Contains("html", StringComparison.OrdinalIgnoreCase) && !type.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                    return r;

                // read at most ~1.5 MB
                await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
                var buf = new byte[1_500_000];
                var total = 0;
                int read;
                while (total < buf.Length && (read = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), cts.Token)) > 0) total += read;
                var html = Encoding.UTF8.GetString(buf, 0, total);
                var text = ExtractText(html, chars);
                return text.Length > 0 ? r with { Content = text } : r;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return r; }
        }

        var tasks = results.Select((r, i) => i < n ? One(r) : Task.FromResult(r)).ToArray();
        return (await Task.WhenAll(tasks)).ToList();
    }

    /// <summary>Only http(s), no local/private addresses, not on the user's block list.</summary>
    public static bool IsAllowed(string url, IReadOnlyCollection<string>? blocked = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https")) return false;
        var host = u.Host.ToLowerInvariant();
        if (host == "localhost" || host.EndsWith(".local") || host.EndsWith(".internal")) return false;
        if (System.Net.IPAddress.TryParse(host, out var ip))
        {
            if (System.Net.IPAddress.IsLoopback(ip)) return false;
            var b = ip.GetAddressBytes();
            if (b.Length == 4 && (b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 169 && b[1] == 254) ||
                                  (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168))) return false;
            if (b.Length == 16) return false; // IPv6 literals: not worth the risk
        }
        return blocked == null || !blocked.Any(d => host == d || host.EndsWith("." + d));
    }

    static readonly Regex DropBlocks = new(@"<(script|style|noscript|svg|nav|header|footer|aside|form|iframe|template)\b[^>]*>.*?</\1\s*>|<!--.*?-->",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex MainBlock = new(@"<(article|main)\b[^>]*>(.*?)</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex BreakTags = new(@"</?(p|div|br|li|tr|h[1-6]|section|blockquote|pre|table|ul|ol)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Readable text of an HTML page (main article if there is one), cut to maxChars.</summary>
    public static string ExtractText(string html, int maxChars)
    {
        html = DropBlocks.Replace(html, " ");
        var main = MainBlock.Match(html);
        if (main.Success && main.Groups[2].Length > 400) html = main.Groups[2].Value;

        html = BreakTags.Replace(html, "\n");
        var text = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " "));
        var lines = text.Split('\n').Select(l => Regex.Replace(l, @"\s+", " ").Trim()).Where(l => l.Length > 1);
        text = string.Join("\n", lines);
        return text.Length > maxChars ? text[..maxChars].TrimEnd() + "…" : text;
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

    public const string DefaultPrompt =
        "Web search results for the user's latest message (today is {date}). " +
        "Use them when they help, prefer them over your own memory for recent facts, and cite sources like [1] or [2]. " +
        "If the results do not answer the question, say so instead of guessing.\n\n{results}";

    /// <summary>Text added to the system prompt. The template may use {date} and {results}.</summary>
    public static string BuildContext(IReadOnlyList<SearchResult> results, DateTime today, string? template = null)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            if (i > 0) sb.AppendLine();
            sb.AppendLine($"[{i + 1}] {r.Title}").AppendLine(r.Url);
            if (r.Snippet.Length > 0) sb.AppendLine(r.Snippet);
            if (r.Content.Length > 0) sb.AppendLine("Page text:").AppendLine(r.Content);
        }
        var t = string.IsNullOrWhiteSpace(template) ? DefaultPrompt : template;
        if (!t.Contains("{results}")) t += "\n\n{results}";
        return t.Replace("{date}", today.ToString("yyyy-MM-dd")).Replace("{results}", sb.ToString().TrimEnd()).Trim();
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
