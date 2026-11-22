using System.Text;
using System.Text.Json.Nodes;

namespace AiChat.Services;

/// <summary>A value the user must give for a catalog app (a folder, a token, ...).</summary>
public record AppField(string Key, string Label, string Placeholder = "", bool Required = true, bool Secret = false);

/// <summary>A ready MCP server for a program or service. {key} in Args / Url / Env / Headers is replaced by the field values.</summary>
public record AppEntry(
    string Id, string Icon, string Name, string Description,
    string Transport,                 // "stdio" | "http"
    string Command = "", string[]? Args = null, string Env = "",
    string Url = "", string Headers = "",
    AppField[]? Fields = null,
    string Setup = "",                // what has to be installed first
    bool Ask = true,                  // ask before every tool call
    string Home = "")
{
    public string[] ArgList => Args ?? Array.Empty<string>();
    public AppField[] FieldList => Fields ?? Array.Empty<AppField>();
}

public static class AppCatalog
{
    public static readonly AppEntry[] All =
    {
        new("vs-universal", "🟣", "Visual Studio (UniversalVSMCP)",
            "کنترل Visual Studio: باز کردن سولوشن، خواندن و نوشتن فایل، بیلد و خواندن خطاها، دیباگ.",
            "stdio", "universal-vsmcp", new[] { "--stdio" }, "VS_AUTO_DETECT=true",
            Setup: "dotnet tool install -g UniversalVSMCP  (needs the .NET SDK and Visual Studio 2022 17.14+ or 2026; Visual Studio must be open)",
            Home: "https://www.nuget.org/packages/UniversalVSMCP"),

        new("filesystem", "📁", "Filesystem (files of a folder)",
            "خواندن، جستجو و (با اجازه‌ی شما) نوشتن فایل‌ها فقط داخل یک پوشه.",
            "stdio", "npx", new[] { "-y", "@modelcontextprotocol/server-filesystem", "{folder}" },
            Fields: new[] { new AppField("folder", "پوشه‌ای که مدل به آن دسترسی دارد", @"C:\Users\me\Documents") },
            Setup: "Node.js (npx)", Home: "https://github.com/modelcontextprotocol/servers"),

        new("git", "🌿", "Git (a repository)",
            "وضعیت، تفاوت‌ها، تاریخچه و commit یک مخزن Git.",
            "stdio", "uvx", new[] { "mcp-server-git", "--repository", "{repo}" },
            Fields: new[] { new AppField("repo", "مسیر مخزن Git", @"C:\Projects\MyApp") },
            Setup: "uv (uvx):  winget install astral-sh.uv", Home: "https://github.com/modelcontextprotocol/servers"),

        new("fetch", "🌐", "Fetch (read web pages)",
            "خواندن یک صفحه‌ی وب و تبدیل آن به متن/Markdown.",
            "stdio", "uvx", new[] { "mcp-server-fetch" }, Ask: false,
            Setup: "uv (uvx):  winget install astral-sh.uv"),

        new("time", "🕒", "Time",
            "ساعت و تاریخ و تبدیل منطقه‌ی زمانی.",
            "stdio", "uvx", new[] { "mcp-server-time" }, Ask: false,
            Setup: "uv (uvx):  winget install astral-sh.uv"),

        new("memory", "🧠", "Memory (knowledge graph)",
            "حافظه‌ی بلندمدت به شکل گراف دانش که بین گفتگوها می‌ماند.",
            "stdio", "npx", new[] { "-y", "@modelcontextprotocol/server-memory" }, Ask: false,
            Setup: "Node.js (npx)"),

        new("thinking", "💭", "Sequential Thinking",
            "کمک به مدل برای حل مسئله‌ی چندمرحله‌ای.",
            "stdio", "npx", new[] { "-y", "@modelcontextprotocol/server-sequential-thinking" }, Ask: false,
            Setup: "Node.js (npx)"),

        new("playwright", "🎭", "Playwright (control a browser)",
            "باز کردن مرورگر، رفتن به صفحه‌ها، کلیک و پر کردن فرم و گرفتن خروجی صفحه.",
            "stdio", "npx", new[] { "@playwright/mcp@latest" },
            Setup: "Node.js (npx); the first run downloads a browser", Home: "https://github.com/microsoft/playwright-mcp"),

        new("sqlite", "🗄️", "SQLite (a database file)",
            "پرسیدن و تغییر یک فایل پایگاه‌داده‌ی SQLite.",
            "stdio", "uvx", new[] { "mcp-server-sqlite", "--db-path", "{db}" },
            Fields: new[] { new AppField("db", "مسیر فایل .db", @"C:\Data\app.db") },
            Setup: "uv (uvx):  winget install astral-sh.uv"),

        new("blender", "🧊", "Blender",
            "ساخت و تغییر صحنه‌ی سه‌بعدی در Blender.",
            "stdio", "uvx", new[] { "blender-mcp" },
            Setup: "uv (uvx) and the \"BlenderMCP\" add-on installed in Blender and started (see the project page)",
            Home: "https://github.com/ahujasid/blender-mcp"),

        new("notion", "📝", "Notion",
            "خواندن و ساخت صفحه‌ها و پایگاه‌داده‌های Notion.",
            "stdio", "npx", new[] { "-y", "@notionhq/notion-mcp-server" }, "NOTION_TOKEN={token}",
            Fields: new[] { new AppField("token", "Notion integration token (ntn_… / secret_…)", "ntn_...", Secret: true) },
            Setup: "Node.js (npx) and an integration made at notion.so/profile/integrations", Home: "https://github.com/makenotion/notion-mcp-server"),

        new("github", "🐙", "GitHub (remote)",
            "مخزن‌ها، issueها، pull requestها و کد در GitHub.",
            "http", Url: "https://api.githubcopilot.com/mcp/", Headers: "Authorization: Bearer {token}",
            Fields: new[] { new AppField("token", "GitHub personal access token", "github_pat_...", Secret: true) },
            Home: "https://github.com/github/github-mcp-server"),

        new("context7", "📚", "Context7 (library docs)",
            "مستندات به‌روز کتابخانه‌ها و فریم‌ورک‌ها برای کدنویسی.",
            "http", Url: "https://mcp.context7.com/mcp", Ask: false,
            Home: "https://context7.com"),

        new("desktop-commander", "💻", "Desktop Commander (terminal and files)",
            "اجرای دستور در ترمینال و کار با فایل‌های کامپیوتر. بسیار قدرتمند؛ فقط با «پرسیدن قبل از اجرا».",
            "stdio", "npx", new[] { "-y", "@wonderwhy-er/desktop-commander@latest" },
            Setup: "Node.js (npx)", Home: "https://github.com/wonderwhy-er/DesktopCommanderMCP"),
    };

    public static string Fill(string template, IReadOnlyDictionary<string, string> values)
    {
        var s = template;
        foreach (var kv in values) s = s.Replace("{" + kv.Key + "}", kv.Value);
        return s;
    }

    /// <summary>Makes the server config of an entry. Error text when a needed field is empty.</summary>
    public static (McpServerConfig? Config, string? Error) Build(AppEntry e, IReadOnlyDictionary<string, string> values, IEnumerable<string> existingNames)
    {
        var vals = e.FieldList.ToDictionary(f => f.Key, f => values.TryGetValue(f.Key, out var v) ? v.Trim().Trim('"') : "");
        var missing = e.FieldList.FirstOrDefault(f => f.Required && vals[f.Key].Length == 0);
        if (missing != null) return (null, missing.Label);

        var name = e.Name;
        var taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        for (var i = 2; taken.Contains(name); i++) name = $"{e.Name} {i}";

        var cfg = new McpServerConfig { Name = name, Transport = e.Transport, Ask = e.Ask, Enabled = true };
        if (e.Transport == "stdio")
        {
            cfg.Command = e.Command;
            cfg.Args = e.ArgList.Select(a => Fill(a, vals)).ToList();
            cfg.Env = Fill(e.Env, vals);
        }
        else
        {
            cfg.Url = e.Url;
            cfg.Headers = Fill(e.Headers, vals);
        }
        return (cfg, null);
    }

    /// <summary>The servers in the common "mcpServers" format (to copy to another program). Secrets are replaced unless asked.</summary>
    public static string ToMcpJson(IEnumerable<McpServerConfig> servers, bool includeSecrets)
    {
        var root = new JsonObject();
        foreach (var s in servers)
        {
            var o = new JsonObject();
            if (s.IsStdio)
            {
                o["command"] = s.Command;
                o["args"] = new JsonArray(s.Args.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
                var env = Pairs(s.Env, '=', includeSecrets);
                if (env.Count > 0) o["env"] = env;
                if (!string.IsNullOrWhiteSpace(s.WorkDir)) o["cwd"] = s.WorkDir;
            }
            else
            {
                o["url"] = s.Url;
                var h = Pairs(s.Headers, ':', includeSecrets);
                if (h.Count > 0) o["headers"] = h;
            }
            var key = s.Name;
            for (var i = 2; root.ContainsKey(key); i++) key = $"{s.Name} {i}";
            root[key] = o;
        }
        return new JsonObject { ["mcpServers"] = root }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    static JsonObject Pairs(string text, char sep, bool secrets)
    {
        var o = new JsonObject();
        foreach (var line in (text ?? "").Replace("\r", "").Split('\n'))
        {
            var i = line.IndexOf(sep);
            if (i <= 0) continue;
            o[line[..i].Trim()] = secrets ? line[(i + 1)..].Trim() : "YOUR_VALUE";
        }
        return o;
    }
}
