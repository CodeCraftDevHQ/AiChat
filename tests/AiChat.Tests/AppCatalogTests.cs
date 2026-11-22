using System.Text.RegularExpressions;
using AiChat.Services;
using Xunit;

namespace AiChat.Tests;

public class AppCatalogTests
{
    [Fact]
    public void Every_entry_is_complete_and_consistent()
    {
        Assert.Equal(AppCatalog.All.Length, AppCatalog.All.Select(a => a.Id).Distinct().Count());
        foreach (var a in AppCatalog.All)
        {
            Assert.NotEmpty(a.Name);
            Assert.NotEmpty(a.Description);
            if (a.Transport == "stdio") Assert.NotEmpty(a.Command);
            else Assert.StartsWith("https://", a.Url);
            // every {placeholder} must have a field
            var text = string.Join("\n", a.ArgList.Concat(new[] { a.Env, a.Url, a.Headers }));
            foreach (Match m in Regex.Matches(text, @"\{(\w+)\}"))
                Assert.Contains(a.FieldList, f => f.Key == m.Groups[1].Value);
        }
    }

    [Fact]
    public void Folder_value_goes_into_the_arguments()
    {
        var fs = AppCatalog.All.First(a => a.Id == "filesystem");
        var (cfg, err) = AppCatalog.Build(fs, new Dictionary<string, string> { ["folder"] = "\"C:\\My Docs\"" }, Array.Empty<string>());
        Assert.Null(err);
        Assert.Equal("npx", cfg!.Command);
        Assert.Equal("stdio", cfg.Transport);
        Assert.Equal(@"C:\My Docs", cfg.Args.Last());
        Assert.True(cfg.Ask);
        Assert.True(cfg.Enabled);
    }

    [Fact]
    public void Missing_required_value_is_reported()
    {
        var fs = AppCatalog.All.First(a => a.Id == "filesystem");
        var (cfg, err) = AppCatalog.Build(fs, new Dictionary<string, string>(), Array.Empty<string>());
        Assert.Null(cfg);
        Assert.Equal(fs.FieldList[0].Label, err);
    }

    [Fact]
    public void Token_goes_into_headers_and_env()
    {
        var gh = AppCatalog.All.First(a => a.Id == "github");
        var (c1, _) = AppCatalog.Build(gh, new Dictionary<string, string> { ["token"] = "abc" }, Array.Empty<string>());
        Assert.Equal("Authorization: Bearer abc", c1!.Headers);
        Assert.Equal("http", c1.Transport);
        var notion = AppCatalog.All.First(a => a.Id == "notion");
        var (c2, _) = AppCatalog.Build(notion, new Dictionary<string, string> { ["token"] = "ntn_1" }, Array.Empty<string>());
        Assert.Equal("NOTION_TOKEN=ntn_1", c2!.Env);
    }

    [Fact]
    public void Names_stay_unique()
    {
        var fetch = AppCatalog.All.First(a => a.Id == "fetch");
        var (c, _) = AppCatalog.Build(fetch, new Dictionary<string, string>(), new[] { fetch.Name.ToUpperInvariant() });
        Assert.Equal(fetch.Name + " 2", c!.Name);
    }

    [Fact]
    public void Visual_studio_entry_needs_no_fields()
    {
        var vs = AppCatalog.All.First(a => a.Id == "vs-universal");
        var (c, err) = AppCatalog.Build(vs, new Dictionary<string, string>(), Array.Empty<string>());
        Assert.Null(err);
        Assert.Equal("universal-vsmcp", c!.Command);
        Assert.Equal(new[] { "--stdio" }, c.Args.ToArray());
        Assert.Contains("VS_AUTO_DETECT=true", c.Env);
    }

    [Fact]
    public void Export_hides_secrets_and_imports_back()
    {
        var servers = new List<McpServerConfig>
        {
            new() { Name = "gh", Url = "https://x.example/mcp", Headers = "Authorization: Bearer SECRET" },
            new() { Name = "fs", Transport = "stdio", Command = "npx", Args = new() { "-y", "pkg", "C:\\a b" }, Env = "KEY=SECRET2", WorkDir = "C:\\w" }
        };
        var safe = AppCatalog.ToMcpJson(servers, false);
        Assert.DoesNotContain("SECRET", safe);
        Assert.Contains("YOUR_VALUE", safe);

        var full = AppCatalog.ToMcpJson(servers, true);
        Assert.Contains("SECRET2", full);
        var back = McpToolBuilder.ImportJson(full, out var skipped, true);
        Assert.Empty(skipped);
        Assert.Equal(2, back.Count);
        var gh = back.First(b => b.Name == "gh");
        Assert.Equal("https://x.example/mcp", gh.Url);
        Assert.Contains("Bearer SECRET", gh.Headers);
        var fs = back.First(b => b.Name == "fs");
        Assert.Equal(new[] { "-y", "pkg", "C:\\a b" }, fs.Args.ToArray());
        Assert.Equal("KEY=SECRET2", fs.Env);
        Assert.Equal("C:\\w", fs.WorkDir);
    }
}
