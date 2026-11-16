using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AiChat.Services;

public record CatalogItem(string Name, string Description, string Author, string Url);

/// <summary>What the user should know before using a downloaded connector.</summary>
public record ConnectorInfo(string Name, string BaseUrl, string Help, List<string> Hosts);

/// <summary>Connectors from a URL or from a catalog (for example a GitHub repository of ready-made connectors).</summary>
public static class ConnectorRepo
{
    /// <summary>Default catalog; the user can change it in the dialog.</summary>
    public const string DefaultCatalog = "https://raw.githubusercontent.com/CodeCraftDevHQ/AiChat-connectors/main/index.json";

    public const int MaxBytes = 1_000_000;

    static readonly Regex GitHubBlob = new(@"^https?://github\.com/([^/]+)/([^/]+)/blob/(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Gist = new(@"^https?://gist\.github\.com/([^/]+)/([0-9a-f]+)/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A GitHub page link becomes the link of the raw file.</summary>
    public static string NormalizeUrl(string url)
    {
        url = (url ?? "").Trim();
        var m = GitHubBlob.Match(url);
        if (m.Success) return $"https://raw.githubusercontent.com/{m.Groups[1].Value}/{m.Groups[2].Value}/{m.Groups[3].Value}";
        var g = Gist.Match(url);
        if (g.Success) return $"https://gist.githubusercontent.com/{g.Groups[1].Value}/{g.Groups[2].Value}/raw";
        return url;
    }

    public static async Task<string> FetchAsync(HttpClient http, string url, CancellationToken ct)
    {
        url = NormalizeUrl(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            throw new InvalidOperationException("Enter a valid http(s) address.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");

        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        var buf = new byte[MaxBytes + 1];
        var total = 0;
        int n;
        while (total < buf.Length && (n = await stream.ReadAsync(buf.AsMemory(total), cts.Token)) > 0) total += n;
        if (total > MaxBytes) throw new InvalidOperationException("The file is too large (over 1 MB).");
        return Encoding.UTF8.GetString(buf, 0, total).TrimStart('﻿');
    }

    /// <summary>Reads a catalog: { "connectors": [ { name, description, author, url } ] } or just the array.</summary>
    public static List<CatalogItem> ParseCatalog(string json, string catalogUrl)
    {
        var list = new List<CatalogItem>();
        var root = ConnectorSpec.ParseLenient(json) ?? throw new FormatException("The catalog is not valid JSON.");
        var arr = root as JsonArray ?? (root as JsonObject)?["connectors"] as JsonArray
                  ?? throw new FormatException("The catalog needs a \"connectors\" list.");
        Uri.TryCreate(NormalizeUrl(catalogUrl), UriKind.Absolute, out var baseUri);

        foreach (var item in arr.OfType<JsonObject>())
        {
            var url = JPath.Text(item["url"]).Trim();
            if (url.Length == 0) continue;
            if (!Uri.TryCreate(url, UriKind.Absolute, out _) && baseUri != null && Uri.TryCreate(baseUri, url, out var rel))
                url = rel.ToString();
            var name = JPath.Text(item["name"]).Trim();
            list.Add(new CatalogItem(name.Length > 0 ? name : url, JPath.Text(item["description"]).Trim(), JPath.Text(item["author"]).Trim(), url));
        }
        return list;
    }

    /// <summary>Checks a downloaded connector and lists the servers it will talk to (the user should see this before using it).</summary>
    public static ConnectorInfo? Inspect(string json, out string? error)
    {
        error = ConnectorSpec.Validate(json);
        if (error != null) return null;
        var spec = ConnectorSpec.TryParse(json, out _)!;
        var hosts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        void Walk(JsonNode? node, string? key)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var (k, v) in o)
                        if (!k.StartsWith('_') || k == "_baseUrl") Walk(v, k);
                    break;
                case JsonArray a:
                    foreach (var v in a) Walk(v, key);
                    break;
                case JsonValue v when v.TryGetValue<string>(out var s) &&
                                      (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)):
                    var m = Regex.Match(s, @"^https?://([^/?#{]+)", RegexOptions.IgnoreCase);
                    if (m.Success) hosts.Add(m.Groups[1].Value);
                    break;
            }
        }
        Walk(spec, null);

        return new ConnectorInfo(JPath.Text(spec["_name"]).Trim(), JPath.Text(spec["_baseUrl"]).Trim(), JPath.Text(spec["_help"]).Trim(), hosts.ToList());
    }
}
