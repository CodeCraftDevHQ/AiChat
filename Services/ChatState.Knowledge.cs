using System.Text.Json.Nodes;
using AiChat.Models;

namespace AiChat.Services;

/// <summary>Project knowledge: documents of a project are indexed and the best passages are found for every question.</summary>
public partial class ChatState
{
    readonly Dictionary<string, KnowledgeSearcher> _kb = new();
    readonly SemaphoreSlim _kbLock = new(1, 1);

    public bool KnowledgeBusy { get; private set; }
    /// <summary>Progress / result line of the running (or last) indexing.</summary>
    public string? KnowledgeStatus { get; private set; }

    static readonly string[] SkipDirs = { "bin", "obj", "node_modules", ".git", ".vs", ".idea", "packages", "__pycache__" };

    async Task<KnowledgeSearcher> KbAsync(string projectId)
    {
        if (_kb.TryGetValue(projectId, out var s)) return s;
        var idx = await _storage.LoadKnowledgeAsync(projectId);
        return _kb[projectId] = new KnowledgeSearcher(idx);
    }

    public async Task<KnowledgeIndex> GetKnowledgeIndexAsync(string projectId) => (await KbAsync(projectId)).Index;

    async Task<bool> HasKnowledgeAsync(Project p) => (await KbAsync(p.Id)).Index.Chunks.Count > 0;

    void DeleteKnowledgeIndex(string projectId)
    {
        _kb.Remove(projectId);
        _storage.DeleteKnowledge(projectId);
    }

    async Task SaveKbAsync(string projectId, KnowledgeIndex idx)
    {
        _kb[projectId] = new KnowledgeSearcher(idx);
        await _storage.SaveKnowledgeAsync(projectId, idx);
    }

    // ------------------------------------------------------------ files of a folder

    public static List<FoundFile> ScanFolder(KnowledgeOptions o)
    {
        var list = new List<FoundFile>();
        if (string.IsNullOrWhiteSpace(o.Folder) || !Directory.Exists(o.Folder)) return list;
        var exts = KnowledgeIndexer.ExtSet(o.Extensions);
        if (exts.Contains(".docx")) exts.UnionWith(new[] { ".pdf", ".pptx", ".xlsx" }); // projects made before these formats existed
        var maxBytes = Math.Max(1, o.MaxFileKb) * 1024L;

        void Walk(string dir)
        {
            if (list.Count >= o.MaxFiles) return;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    if (list.Count >= o.MaxFiles) return;
                    if (!exts.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
                    var fi = new FileInfo(f);
                    if (fi.Length == 0 || fi.Length > maxBytes) continue;
                    list.Add(new FoundFile(f, fi.Name, fi.LastWriteTimeUtc.Ticks, fi.Length));
                }
                if (!o.Subfolders) return;
                foreach (var d in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(d);
                    if (SkipDirs.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    Walk(d);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        }
        Walk(o.Folder);
        return list;
    }

    // ------------------------------------------------------------ indexing

    /// <summary>Reads the folder (changed files only) and, when enabled, makes the embeddings of new passages. rebuild = read every file again.</summary>
    public async Task<string> IndexKnowledgeAsync(Project p, bool rebuild)
    {
        if (KnowledgeBusy) return L["یک نمایه‌سازی در حال انجام است."];
        KnowledgeBusy = true;
        KnowledgeStatus = L["در حال خواندن فایل‌ها…"];
        Notify();
        await _kbLock.WaitAsync();
        try
        {
            var o = p.Knowledge;
            var idx = (await KbAsync(p.Id)).Index;
            var hadFolder = idx.Docs.Any(d => d.Source == "folder");
            if (rebuild) foreach (var d in idx.Docs.Where(d => d.Source == "folder")) d.Ticks = -1;

            var found = await Task.Run(() => ScanFolder(o));
            var stats = await Task.Run(() => KnowledgeIndexer.SyncFolder(idx, found, f => { try { return File.ReadAllBytes(f); } catch { return null; } }, o));
            var msg = o.Folder.Trim().Length == 0 && !hadFolder
                ? ""
                : L.F("پوشه: {0} جدید، {1} تغییر کرده، {2} حذف‌شده، {3} بدون تغییر، {4} ردشده.", stats.Added, stats.Updated, stats.Removed, stats.Unchanged, stats.Skipped);
            if (o.Folder.Trim().Length > 0 && !Directory.Exists(o.Folder)) msg = L["پوشه پیدا نشد (روی اندروید پوشه‌ی کامپیوتر در دسترس نیست)."];

            if (o.UseEmbeddings) msg = (msg + " " + await EmbedMissingAsync(p, idx)).Trim();
            await SaveKbAsync(p.Id, idx);
            msg = (msg + " " + L.F("جمع: {0} سند، {1} تکه.", idx.Docs.Count, idx.Chunks.Count)).Trim();
            KnowledgeStatus = msg;
            return msg;
        }
        catch (Exception ex)
        {
            _log.Warn("Knowledge indexing failed: " + ex.Message);
            KnowledgeStatus = L["نمایه‌سازی ناموفق بود: "] + ex.Message;
            return KnowledgeStatus;
        }
        finally
        {
            _kbLock.Release();
            KnowledgeBusy = false;
            Notify();
        }
    }

    /// <summary>A document picked by the user (a file in the project's knowledge, not sent with every message).</summary>
    public async Task<string?> AddKnowledgeUploadAsync(Project p, string name, byte[] bytes)
    {
        await _kbLock.WaitAsync();
        try
        {
            var idx = (await KbAsync(p.Id)).Index;
            if (!KnowledgeIndexer.AddUpload(idx, name, bytes, p.Knowledge))
                return L.F("«{0}» متن قابل خواندن ندارد (فقط متن، کد، html و docx).", name);
            string? warn = null;
            if (p.Knowledge.UseEmbeddings)
            {
                var r = await EmbedMissingAsync(p, idx);
                if (r.Length > 0) warn = r;
            }
            await SaveKbAsync(p.Id, idx);
            Notify();
            return warn;
        }
        catch (Exception ex) { _log.Warn($"Adding '{name}' to the knowledge base failed: " + ex.Message); return ex.Message; }
        finally { _kbLock.Release(); }
    }

    public async Task RemoveKnowledgeDocAsync(Project p, int docIndex)
    {
        await _kbLock.WaitAsync();
        try
        {
            var idx = (await KbAsync(p.Id)).Index;
            if (docIndex < 0 || docIndex >= idx.Docs.Count) return;
            KnowledgeIndexer.RemoveDoc(idx, docIndex);
            await SaveKbAsync(p.Id, idx);
            Notify();
        }
        finally { _kbLock.Release(); }
    }

    public async Task ClearKnowledgeAsync(Project p)
    {
        await _kbLock.WaitAsync();
        try
        {
            await SaveKbAsync(p.Id, new KnowledgeIndex());
            KnowledgeStatus = null;
            Notify();
        }
        finally { _kbLock.Release(); }
    }

    // ------------------------------------------------------------ embeddings

    ProviderConfig? EmbedProvider(KnowledgeOptions o) => Providers.FirstOrDefault(x => x.Id == o.EmbedProviderId);

    async Task<List<float[]>> EmbedAsync(KnowledgeOptions o, IReadOnlyList<string> texts, CancellationToken ct)
    {
        var cfg = EmbedProvider(o) ?? throw new InvalidOperationException(L["برای جستجوی معنایی یک اتصال (API) انتخاب کنید."]);
        using var req = new HttpRequestMessage(HttpMethod.Post, ProviderHttp.Url(cfg, "/embeddings"))
        {
            Content = new StringContent(KnowledgeEmbed.Body(o.EmbedModel, texts).ToJsonString(), System.Text.Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(cfg.ApiKey)) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cfg.ApiKey.Trim());
        ProviderHttp.Customize(req, cfg);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        using var resp = await _http.SendAsync(req, cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{(int)resp.StatusCode}: {ProviderHttp.ExtractError(body)}");
        var vecs = KnowledgeEmbed.Parse(body);
        if (vecs.Count != texts.Count) throw new InvalidOperationException($"{vecs.Count} / {texts.Count}");
        return vecs;
    }

    /// <summary>Makes the vectors of passages that have none. Returns "" or a short warning.</summary>
    async Task<string> EmbedMissingAsync(Project p, KnowledgeIndex idx)
    {
        var o = p.Knowledge;
        if (EmbedProvider(o) == null) return L["جستجوی معنایی روشن است ولی اتصالی برای آن انتخاب نشده."];
        if (idx.EmbedModel != o.EmbedModel)
        {
            foreach (var c in idx.Chunks) c.V = null;
            idx.EmbedModel = o.EmbedModel;
        }
        var todo = idx.Chunks.Where(c => c.V == null).ToList();
        var batch = Math.Clamp(o.EmbedBatch, 1, 128);
        try
        {
            for (var i = 0; i < todo.Count; i += batch)
            {
                KnowledgeStatus = L.F("در حال ساخت بردارها: {0} از {1}…", Math.Min(i, todo.Count), todo.Count);
                Notify();
                var part = todo.Skip(i).Take(batch).ToList();
                var vecs = await EmbedAsync(o, part.Select(c => c.T).ToList(), CancellationToken.None);
                for (var j = 0; j < part.Count; j++) part[j].V = KnowledgeSearcher.EncodeVec(vecs[j]);
            }
            return "";
        }
        catch (Exception ex)
        {
            _log.Warn("Embeddings failed: " + ex.Message);
            return L["ساخت بردارها ناموفق بود (جستجوی کلمه‌ای کار می‌کند): "] + ex.Message;
        }
    }

    /// <summary>Tests the embeddings settings with one short text.</summary>
    public async Task<string> TestEmbeddingsAsync(KnowledgeOptions o)
    {
        try
        {
            var v = await EmbedAsync(o, new[] { "test" }, CancellationToken.None);
            return L.F("درست کار می‌کند ✓ (اندازه‌ی بردار: {0})", v[0].Length);
        }
        catch (Exception ex) { return L["ناموفق: "] + ex.Message; }
    }

    // ------------------------------------------------------------ search

    public async Task<List<KnowledgeHit>> SearchKnowledgeAsync(Project p, string query, int k, CancellationToken ct)
    {
        var s = await KbAsync(p.Id);
        if (s.Index.Chunks.Count == 0) return new();
        float[]? qv = null;
        var o = p.Knowledge;
        if (o.UseEmbeddings && s.HasVectors && EmbedProvider(o) != null && s.Index.EmbedModel == o.EmbedModel)
        {
            try { qv = (await EmbedAsync(o, new[] { query.Length > 2000 ? query[..2000] : query }, ct))[0]; }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.Warn("Query embedding failed: " + ex.Message); }
        }
        return s.Search(query, Math.Clamp(k, 1, 20), qv);
    }

    ToolDef KnowledgeTool(Project p, List<KnowledgeHit> sink) => new()
    {
        Name = "search_project_documents",
        Label = "Project documents",
        Description = $"Searches the documents of the project \"{p.Name}\" (notes, files, code) and returns the most relevant passages with their source. " +
                      "Call it whenever the answer may be in those documents; search again with different words if the first result is not enough.",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["query"] = new JsonObject { ["type"] = "string", ["description"] = "What to look for, in the language of the documents" }
            },
            ["required"] = new JsonArray("query")
        },
        Run = async (a, ct) =>
        {
            var q = a["query"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(q)) return "Empty query.";
            var hits = await SearchKnowledgeAsync(p, q, p.Knowledge.TopK, ct);
            if (hits.Count == 0) return "No matching passages.";
            foreach (var h in hits) if (!sink.Any(x => x.Text == h.Text)) sink.Add(h);
            return KnowledgePrompt.Context(hits, p.Knowledge.MaxContextChars);
        }
    };

    // ------------------------------------------------------------ other chats of the project

    List<Conversation> OtherChats(Project p, Conversation? current) =>
        Conversations.Where(c => c.ProjectId == p.Id && !ReferenceEquals(c, current) && c.Messages.Count > 0).OrderByDescending(c => c.Updated).ToList();

    (string Sig, KnowledgeSearcher S)? _chatIdx;

    /// <summary>Search over the other chats (index is rebuilt only when a chat changed).</summary>
    List<KnowledgeHit> SearchChats(Project p, Conversation? current, string query, int k)
    {
        var chats = OtherChats(p, current);
        if (chats.Count == 0) return new();
        var sig = p.Id + "|" + p.Knowledge.ChunkChars + "|" + string.Join(",", chats.Select(c => c.Id + ":" + c.Messages.Count + ":" + c.Updated.Ticks));
        if (_chatIdx is not { } cur || cur.Sig != sig)
            _chatIdx = (sig, new KnowledgeSearcher(KnowledgeChats.BuildIndex(chats, p.Knowledge)));
        return _chatIdx.Value.S.Search(query, k);
    }

    IEnumerable<ToolDef> ChatTools(Project p, Conversation conv, List<KnowledgeHit> sink)
    {
        if (OtherChats(p, conv).Count == 0) yield break;
        var limit = Math.Clamp(ToolOpts.ResultChars, 1500, 50000) - 400;

        yield return new ToolDef
        {
            Name = "list_project_chats",
            Label = "Project chats",
            Description = $"Lists the other chats of the project \"{p.Name}\" (title, date, number of messages). Use it when the user refers to another chat of this project.",
            Parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            Run = (_, _) => Task.FromResult(string.Join("\n", OtherChats(p, conv).Take(60)
                .Select((c, i) => $"{i + 1}. {c.Title} — {c.Updated.ToLocalTime():yyyy-MM-dd HH:mm}, {c.Messages.Count} messages")))
        };
        yield return new ToolDef
        {
            Name = "read_project_chat",
            Label = "Read chat",
            Description = "Reads another chat of this project by its title (as in list_project_chats). Starts from the first message; pass from_message=N to continue where the previous part ended, " +
                          "or a negative number (e.g. -10) to read only the last messages. Use it when the user asks to look at, summarize or continue another chat.",
            Parameters = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["title"] = new JsonObject { ["type"] = "string", ["description"] = "Title of the chat (or a distinctive part of it)" },
                    ["from_message"] = new JsonObject { ["type"] = "integer", ["description"] = "1-based message number to start from; negative = the last N messages. Default 1." }
                },
                ["required"] = new JsonArray("title")
            },
            Run = (a, _) =>
            {
                var (chat, err) = KnowledgeChats.Find(OtherChats(p, conv), a["title"]?.ToString() ?? "");
                if (chat == null) return Task.FromResult(err ?? "Not found.");
                var from = a["from_message"] is JsonNode n && int.TryParse(n.ToString(), out var f) ? f : 1;
                var r = KnowledgeChats.Render(chat, from, limit);
                var head = $"Chat \"{chat.Title}\": messages {r.First}-{r.Last} of {r.Total}." + (r.Last < r.Total ? $" Call again with from_message={r.Last + 1} for the rest." : " (end of the chat)");
                return Task.FromResult(head + "\n\n" + r.Text);
            }
        };
        yield return new ToolDef
        {
            Name = "search_project_chats",
            Label = "Search chats",
            Description = "Searches the text of the other chats of this project and returns matching passages with the chat title. Use it to find where something was discussed.",
            Parameters = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string", ["description"] = "What to look for" } },
                ["required"] = new JsonArray("query")
            },
            Run = (a, _) =>
            {
                var hits = SearchChats(p, conv, a["query"]?.ToString() ?? "", p.Knowledge.TopK);
                if (hits.Count == 0) return Task.FromResult("No matching passages in the other chats.");
                foreach (var h in hits) if (!sink.Any(x => x.Text == h.Text)) sink.Add(h);
                return Task.FromResult(KnowledgePrompt.Context(hits, p.Knowledge.MaxContextChars));
            }
        };
    }

    // ------------------------------------------------------------ the project folder (read-only, nothing outside of it)

    IEnumerable<ToolDef> FolderTools(Project p)
    {
        var root = p.Knowledge.Folder?.Trim() ?? "";
        if (root.Length == 0 || !Directory.Exists(root)) yield break;
        var limit = Math.Clamp(ToolOpts.ResultChars, 1500, 50000) - 400;

        yield return new ToolDef
        {
            Name = "list_project_folder",
            Label = "Project folder",
            Description = $"Lists files and sub-folders of the project's folder ({root}) or of a sub-folder of it. Read-only; paths outside this folder are refused.",
            Parameters = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Sub-folder relative to the project folder (empty = the folder itself)" } }
            },
            Run = (a, _) =>
            {
                var full = FolderJail.Resolve(root, a["path"]?.ToString());
                if (full == null) return Task.FromResult("Refused: the path is outside the project folder.");
                if (!Directory.Exists(full)) return Task.FromResult("Folder not found.");
                try
                {
                    var rootFull = Path.GetFullPath(root);
                    var lines = new List<string>();
                    foreach (var d in Directory.EnumerateDirectories(full).Take(200)) lines.Add("[dir]  " + Path.GetRelativePath(rootFull, d));
                    foreach (var f in Directory.EnumerateFiles(full).Take(300)) lines.Add($"{Path.GetRelativePath(rootFull, f)}  ({new FileInfo(f).Length / 1024 + 1} KB)");
                    return Task.FromResult(lines.Count == 0 ? "(empty)" : string.Join("\n", lines));
                }
                catch (Exception ex) { return Task.FromResult("Error: " + ex.Message); }
            }
        };
        yield return new ToolDef
        {
            Name = "read_project_file",
            Label = "Read file",
            Description = "Reads the text of one file inside the project's folder (text, code, pdf, docx, pptx, xlsx). Read-only; paths outside the folder are refused. " +
                          "For long files pass offset (characters) to continue.",
            Parameters = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = "File path relative to the project folder" },
                    ["offset"] = new JsonObject { ["type"] = "integer", ["description"] = "Start character (default 0)" }
                },
                ["required"] = new JsonArray("path")
            },
            Run = (a, _) =>
            {
                var full = FolderJail.Resolve(root, a["path"]?.ToString());
                if (full == null) return Task.FromResult("Refused: the path is outside the project folder.");
                if (!File.Exists(full)) return Task.FromResult("File not found.");
                try
                {
                    var fi = new FileInfo(full);
                    if (fi.Length > 50L * 1024 * 1024) return Task.FromResult("File too large.");
                    var text = KnowledgeText.Extract(fi.Name, File.ReadAllBytes(full));
                    if (text == null) return Task.FromResult("This file has no readable text.");
                    var off = a["offset"] is JsonNode n && int.TryParse(n.ToString(), out var o) ? Math.Clamp(o, 0, text.Length) : 0;
                    var part = text.Substring(off, Math.Min(limit, text.Length - off));
                    var end = off + part.Length;
                    var head = $"{fi.Name}: characters {off}-{end} of {text.Length}." + (end < text.Length ? $" Call again with offset={end} for the rest." : " (end of file)");
                    return Task.FromResult(head + "\n\n" + part);
                }
                catch (Exception ex) { return Task.FromResult("Error: " + ex.Message); }
            }
        };
    }
}
