using System.Text;
using System.Text.Json.Nodes;
using AiChat.Models;
using Microsoft.Maui.Storage;

namespace AiChat.Services;

/// <summary>AiChat as a server for other programs: an OpenAI-compatible API and an MCP server on 127.0.0.1.</summary>
public partial class ChatState
{
    public LocalServerOptions LocalOpts { get; private set; } = new();
    LocalHttpServer? _local;
    public string? LocalError { get; private set; }
    public bool LocalRunning => _local?.Running == true;

    void LoadLocalOpts()
    {
        try { LocalOpts = System.Text.Json.JsonSerializer.Deserialize<LocalServerOptions>(Preferences.Default.Get("local_server", "")) ?? new(); }
        catch { LocalOpts = new(); }
    }

    async Task InitLocalServerAsync()
    {
        LoadLocalOpts();
        LocalOpts.Token = await _storage.LoadLocalTokenAsync();
        if (string.IsNullOrEmpty(LocalOpts.Token))
        {
            LocalOpts.Token = LocalApi.NewToken();
            await _storage.SaveLocalTokenAsync(LocalOpts.Token);
        }
        ApplyLocalServer();
    }

    /// <summary>Saves the options; the server is (re)started or stopped to match.</summary>
    public void SaveLocalOpts(bool restart = false)
    {
        Preferences.Default.Set("local_server", System.Text.Json.JsonSerializer.Serialize(LocalOpts));
        if (restart || LocalOpts.Enabled != LocalRunning) ApplyLocalServer();
        Notify();
    }

    public async Task RegenerateLocalTokenAsync()
    {
        LocalOpts.Token = LocalApi.NewToken();
        await _storage.SaveLocalTokenAsync(LocalOpts.Token);
        Notify();
    }

    void ApplyLocalServer()
    {
        _local?.Dispose();
        _local = null;
        LocalError = null;
        if (!LocalOpts.Enabled) return;
        LocalOpts.Port = Math.Clamp(LocalOpts.Port, 1024, 65535);
        var s = new LocalHttpServer(LocalOpts, new LocalHost(this), m => _log.Info(m));
        LocalError = s.Start();
        if (LocalError == null) { _local = s; _log.Info($"Local server started on 127.0.0.1:{LocalOpts.Port}"); }
        else _log.Warn("Local server could not start: " + LocalError);
    }

    sealed class LocalHost : ILocalHost
    {
        readonly ChatState _s;
        public LocalHost(ChatState s) { _s = s; }

        /// <summary>The lists are changed by the UI thread; copy them (retry if one changes during the copy).</summary>
        static T Snap<T>(Func<T> f)
        {
            for (var i = 0; ; i++)
            {
                try { return f(); }
                catch (InvalidOperationException) when (i < 3) { }
            }
        }

        public IReadOnlyList<ProviderConfig> Providers() => Snap(() => _s.Providers.ToList());

        public IAsyncEnumerable<string> StreamAsync(ProviderConfig provider, string model, IReadOnlyList<ChatMessage> history, string? system, CancellationToken ct)
        {
            var gen = provider.Clone();
            gen.Tools = null;
            return _s._factory.Create(gen.Type).StreamAsync(gen, model, history, system, null, ct);
        }

        static JsonObject Sch(params (string Name, string Type, string Desc, bool Required)[] props)
        {
            var p = new JsonObject();
            var req = new JsonArray();
            foreach (var (n, t, d, r) in props)
            {
                p[n] = new JsonObject { ["type"] = t, ["description"] = d };
                if (r) req.Add(n);
            }
            return new JsonObject { ["type"] = "object", ["properties"] = p, ["required"] = req };
        }

        public IReadOnlyList<McpToolSpec> Tools()
        {
            var o = _s.LocalOpts;
            var l = new List<McpToolSpec>();
            if (o.ToolAsk)
                l.Add(new("ask_ai", "Sends a prompt to one of the AI models configured in the AiChat app and returns the answer.",
                    Sch(("prompt", "string", "The question or task", true), ("model", "string", "Provider/model (see list_models). Empty = the default model", false), ("system", "string", "Optional system instruction", false))));
            if (o.ToolModels)
                l.Add(new("list_models", "Lists the models that can be used with ask_ai (as Provider/model).", Sch()));
            if (o.ToolChats)
            {
                l.Add(new("list_conversations", "Lists chats saved in AiChat (newest first). Optional text filter.",
                    Sch(("query", "string", "Words to look for in the title or messages", false), ("limit", "integer", "Max chats (default 30)", false))));
                l.Add(new("read_conversation", "Reads a saved AiChat chat by its title or id (as in list_conversations).",
                    Sch(("chat", "string", "Title or id of the chat", true), ("from_message", "integer", "1-based first message; negative = the last N messages", false))));
            }
            if (o.ToolProjects)
            {
                l.Add(new("list_projects", "Lists the projects of AiChat and how many documents each has in its knowledge base.", Sch()));
                l.Add(new("search_project_knowledge", "Searches the documents (knowledge base) of an AiChat project and returns matching passages with their source.",
                    Sch(("project", "string", "Project name", true), ("query", "string", "What to look for", true))));
            }
            return l;
        }

        static string Arg(JsonObject a, string k) => a[k] is JsonNode n ? n.ToString() : "";
        static int IntArg(JsonObject a, string k, int def) => a[k] is JsonNode n && int.TryParse(n.ToString(), out var v) ? v : def;

        public async Task<string> CallAsync(string name, JsonObject args, CancellationToken ct)
        {
            var o = _s.LocalOpts;
            switch (name)
            {
                case "ask_ai" when o.ToolAsk:
                {
                    var prompt = Arg(args, "prompt");
                    if (string.IsNullOrWhiteSpace(prompt)) return "Error: prompt is empty.";
                    var (p, model, err) = LocalApi.Resolve(Providers(), Arg(args, "model"), o.DefaultModel);
                    if (p == null) return "Error: " + err;
                    var sys = Arg(args, "system");
                    var sb = new StringBuilder();
                    var strip = new ThinkStripper();
                    await foreach (var chunk in StreamAsync(p, model, new List<ChatMessage> { new() { Role = "user", Content = prompt } }, string.IsNullOrWhiteSpace(sys) ? null : sys, ct))
                        if (!chunk.StartsWith(ProviderStatus.Prefix, StringComparison.Ordinal)) sb.Append(strip.Feed(chunk));
                    sb.Append(strip.Flush());
                    var text = sb.ToString().Trim();
                    return text.Length == 0 ? "(the model returned an empty answer)" : text;
                }
                case "list_models" when o.ToolModels:
                    return string.Join("\n", LocalApi.ModelIds(Providers()));
                case "list_conversations" when o.ToolChats:
                {
                    var q = SearchText.NormalizeQuery(Arg(args, "query"));
                    var limit = Math.Clamp(IntArg(args, "limit", 30), 1, 100);
                    var all = Snap(() => _s.Conversations.ToList());
                    var rows = all.Where(c => q.Length == 0 || SearchText.Count(c.Title, q) > 0 || c.Messages.Any(m => SearchText.Count(m.Content, q) > 0))
                        .OrderByDescending(c => c.Updated).Take(limit)
                        .Select(c => $"{c.Id} | {c.Title} | {c.Updated.ToLocalTime():yyyy-MM-dd HH:mm} | {c.Messages.Count} messages");
                    var text = string.Join("\n", rows);
                    return text.Length == 0 ? "No chats." : text;
                }
                case "read_conversation" when o.ToolChats:
                {
                    var all = Snap(() => _s.Conversations.ToList());
                    var (chat, err) = KnowledgeChats.Find(all, Arg(args, "chat"));
                    if (chat == null) return err ?? "Not found.";
                    var r = KnowledgeChats.Render(chat, IntArg(args, "from_message", 1), 12000);
                    return $"Chat \"{chat.Title}\": messages {r.First}-{r.Last} of {r.Total}." + (r.Last < r.Total ? $" Call again with from_message={r.Last + 1} for the rest." : "") + "\n\n" + r.Text;
                }
                case "list_projects" when o.ToolProjects:
                {
                    var lines = new List<string>();
                    foreach (var p in Snap(() => _s.Projects.ToList()))
                    {
                        var idx = await _s.GetKnowledgeIndexAsync(p.Id);
                        lines.Add($"{p.Name} — {idx.Docs.Count} documents, {idx.Chunks.Count} passages");
                    }
                    return lines.Count == 0 ? "No projects." : string.Join("\n", lines);
                }
                case "search_project_knowledge" when o.ToolProjects:
                {
                    var pq = SearchText.NormalizeQuery(Arg(args, "project"));
                    var projects = Snap(() => _s.Projects.ToList());
                    var proj = projects.FirstOrDefault(p => SearchText.NormalizeQuery(p.Name) == pq)
                               ?? projects.FirstOrDefault(p => SearchText.NormalizeQuery(p.Name).Contains(pq));
                    if (proj == null) return "Project not found. Call list_projects.";
                    var hits = await _s.SearchKnowledgeAsync(proj, Arg(args, "query"), proj.Knowledge.TopK, ct);
                    return hits.Count == 0 ? "No matching passages." : KnowledgePrompt.Context(hits, proj.Knowledge.MaxContextChars);
                }
                default:
                    return "Error: this tool is switched off.";
            }
        }
    }
}
