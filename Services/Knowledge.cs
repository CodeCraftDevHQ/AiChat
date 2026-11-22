using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AiChat.Services;

/// <summary>Settings of the knowledge base (the documents) of one project.</summary>
public class KnowledgeOptions
{
    /// <summary>"auto" = the best passages are added to every question | "tool" = the model searches when it needs | "off".</summary>
    public string Mode { get; set; } = "auto";
    public int TopK { get; set; } = 5;
    public int ChunkChars { get; set; } = 1200;
    public int Overlap { get; set; } = 150;
    /// <summary>The passages added to a question are cut to this many characters in total.</summary>
    public int MaxContextChars { get; set; } = 6000;
    public bool ShowSources { get; set; } = true;

    /// <summary>The model may list / search / read the other chats of this project (tools).</summary>
    public bool ChatTools { get; set; } = true;
    /// <summary>Automatic mode also adds matching passages of the other chats of this project.</summary>
    public bool IncludeChats { get; set; }
    /// <summary>The model may list and read files of the project folder (read-only, only inside that folder).</summary>
    public bool FolderTools { get; set; } = true;

    /// <summary>A folder on this computer (Windows) that is indexed with all its files.</summary>
    public string Folder { get; set; } = "";
    public bool Subfolders { get; set; } = true;
    public string Extensions { get; set; } = "txt,md,markdown,csv,json,xml,html,htm,yaml,yml,ini,log,cs,razor,css,js,ts,tsx,jsx,py,java,c,cpp,h,go,rs,sql,docx,pdf,pptx,xlsx";
    public int MaxFiles { get; set; } = 3000;
    public int MaxFileKb { get; set; } = 4096;

    /// <summary>Also search by meaning with an embeddings API (an OpenAI-compatible /embeddings endpoint).</summary>
    public bool UseEmbeddings { get; set; }
    public string EmbedProviderId { get; set; } = "";
    public string EmbedModel { get; set; } = "text-embedding-3-small";
    public int EmbedBatch { get; set; } = 32;

    public KnowledgeOptions Clone() => JsonSerializer.Deserialize<KnowledgeOptions>(JsonSerializer.Serialize(this))!;
}

public class KnowledgeDoc
{
    public string Name { get; set; } = "";
    /// <summary>"folder" or "upload".</summary>
    public string Source { get; set; } = "upload";
    public string Path { get; set; } = "";
    public long Ticks { get; set; }
    public long Size { get; set; }
}

public class KnowledgeChunk
{
    /// <summary>Index of the document in KnowledgeIndex.Docs.</summary>
    public int D { get; set; }
    public string T { get; set; } = "";
    /// <summary>Embedding (float32, base64) when embeddings are used.</summary>
    public string? V { get; set; }
}

public class KnowledgeIndex
{
    public List<KnowledgeDoc> Docs { get; set; } = new();
    public List<KnowledgeChunk> Chunks { get; set; } = new();
    /// <summary>Model the stored embeddings were made with (they are redone when the model changes).</summary>
    public string EmbedModel { get; set; } = "";
}

public record KnowledgeHit(string Doc, string Text, double Score);

public record FoundFile(string Path, string Name, long Ticks, long Size);

public static class KnowledgeText
{
    static readonly Regex Script = new(@"<(script|style)\b.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.Compiled);
    static readonly Regex BlockEnd = new(@"</(p|div|li|tr|h[1-6]|br|section|article)>|<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Spaces = new(@"[ \t]+", RegexOptions.Compiled);
    static readonly Regex Blank = new(@"\n{3,}", RegexOptions.Compiled);

    /// <summary>Plain text of a file (null = not a readable document).</summary>
    public static string? Extract(string name, byte[] bytes)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        string? text;
        if (ext == ".docx") text = Docx(bytes);
        else if (ext == ".pptx") text = Pptx(bytes);
        else if (ext == ".xlsx") text = Xlsx(bytes);
        else if (ext == ".pdf" || PdfHelper.LooksLikePdf(bytes)) text = PdfHelper.ExtractText(bytes, null, 5_000_000);
        else
        {
            text = DecodeText(bytes);
            if (text == null) return null;
            if (ext is ".html" or ".htm") text = Html(text);
        }
        text = text?.Replace("\r\n", "\n").Replace('\r', '\n');
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>UTF-8 / UTF-16 (with BOM) / Windows-1256 (old Persian files). Null = binary.</summary>
    public static string? DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        var head = Math.Min(bytes.Length, 8192);
        for (var i = 0; i < head; i++) if (bytes[i] == 0) return null;
        var utf8 = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        if (!utf8.Contains('\uFFFD')) return utf8;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1256).GetString(bytes);
        }
        catch { return utf8; }
    }

    public static string Html(string html)
    {
        var t = Script.Replace(html, " ");
        t = BlockEnd.Replace(t, "\n");
        t = Tags.Replace(t, " ");
        t = System.Net.WebUtility.HtmlDecode(t);
        t = Spaces.Replace(t, " ");
        t = Regex.Replace(t, @" ?\n ?", "\n");
        return Blank.Replace(t, "\n\n").Trim();
    }

    static string? Docx(byte[] bytes)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var entry = zip.GetEntry("word/document.xml");
            if (entry == null) return null;
            using var s = entry.Open();
            var doc = System.Xml.Linq.XDocument.Load(s);
            System.Xml.Linq.XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            var sb = new StringBuilder();
            foreach (var p in doc.Descendants(w + "p"))
            {
                foreach (var n in p.Descendants())
                {
                    if (n.Name == w + "t") sb.Append(n.Value);
                    else if (n.Name == w + "tab") sb.Append('\t');
                    else if (n.Name == w + "br") sb.Append('\n');
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException) { return null; }
    }

    static string? OfficeXml(byte[] bytes, Func<string, bool> pick, string textName, string paraName)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var sb = new StringBuilder();
            foreach (var e in zip.Entries.Where(e => pick(e.FullName)).OrderBy(e => e.FullName, StringComparer.Ordinal))
            {
                using var s = e.Open();
                var doc = System.Xml.Linq.XDocument.Load(s);
                foreach (var el in doc.Descendants())
                {
                    if (el.Name.LocalName == textName) sb.Append(el.Value).Append(' ');
                    else if (el.Name.LocalName == paraName) sb.Append('\n');
                }
                sb.Append("\n\n");
            }
            return sb.ToString();
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException) { return null; }
    }

    static string? Pptx(byte[] bytes) =>
        OfficeXml(bytes, n => n.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && n.EndsWith(".xml", StringComparison.Ordinal), "t", "p");

    static string? Xlsx(byte[] bytes) =>
        OfficeXml(bytes, n => n == "xl/sharedStrings.xml", "t", "si");

    /// <summary>Cuts a text into passages of about <paramref name="size"/> characters (at paragraph / line ends when possible) that overlap a little.</summary>
    public static List<string> Chunk(string text, int size, int overlap)
    {
        size = Math.Max(200, size);
        overlap = Math.Clamp(overlap, 0, size / 2);
        var result = new List<string>();
        var cur = new StringBuilder();

        void Flush()
        {
            var t = cur.ToString().Trim();
            if (t.Length > 0) result.Add(t);
            cur.Clear();
            if (overlap > 0 && t.Length > overlap)
            {
                var tail = t[^overlap..];
                var sp = tail.IndexOfAny(new[] { ' ', '\n' });
                cur.Append(sp > 0 && sp < tail.Length - 1 ? tail[(sp + 1)..] : tail).Append('\n');
            }
        }

        foreach (var para in Regex.Split(text.Trim(), @"\n\s*\n"))
        {
            var p = para.Trim();
            if (p.Length == 0) continue;
            // a very long paragraph (minified code, one-line files) is cut by lines, then by characters
            var pieces = p.Length <= size ? new List<string> { p } : SplitLong(p, size);
            foreach (var piece in pieces)
            {
                if (cur.Length > 0 && cur.Length + piece.Length + 2 > size) Flush();
                if (cur.Length > 0) cur.Append("\n\n");
                cur.Append(piece);
            }
        }
        var last = cur.ToString().Trim();
        if (last.Length > 0 && (result.Count == 0 || last.Length > overlap + 20)) result.Add(last);
        return result;
    }

    static List<string> SplitLong(string p, int size)
    {
        var res = new List<string>();
        var cur = new StringBuilder();
        foreach (var line in p.Split('\n'))
        {
            if (line.Length > size)
            {
                if (cur.Length > 0) { res.Add(cur.ToString()); cur.Clear(); }
                for (var i = 0; i < line.Length; i += size) res.Add(line.Substring(i, Math.Min(size, line.Length - i)));
                continue;
            }
            if (cur.Length > 0 && cur.Length + line.Length + 1 > size) { res.Add(cur.ToString()); cur.Clear(); }
            if (cur.Length > 0) cur.Append('\n');
            cur.Append(line);
        }
        if (cur.Length > 0) res.Add(cur.ToString());
        return res;
    }
}

/// <summary>Keeps the index in step with a folder / with uploaded documents.</summary>
public static class KnowledgeIndexer
{
    public record Stats(int Added, int Updated, int Removed, int Unchanged, int Skipped);

    public static HashSet<string> ExtSet(string extensions) =>
        extensions.Split(new[] { ',', ';', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(e => "." + e.Trim().TrimStart('.').ToLowerInvariant()).ToHashSet();

    /// <summary>Adds / re-reads changed / removes vanished documents of the folder source. Unchanged files are kept as they are.</summary>
    public static Stats SyncFolder(KnowledgeIndex idx, IReadOnlyList<FoundFile> found, Func<string, byte[]?> read, KnowledgeOptions o)
    {
        int added = 0, updated = 0, removed = 0, same = 0, skipped = 0;
        var byPath = found.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var drop = new HashSet<int>();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < idx.Docs.Count; i++)
        {
            var d = idx.Docs[i];
            if (d.Source != "folder") continue;
            known.Add(d.Path);
            if (!byPath.TryGetValue(d.Path, out var f)) { drop.Add(i); removed++; }
            else if (f.Ticks != d.Ticks || f.Size != d.Size) drop.Add(i);
            else { keep.Add(d.Path); same++; }
        }
        Remove(idx, drop);

        foreach (var f in found)
        {
            if (keep.Contains(f.Path)) continue;
            var bytes = read(f.Path);
            var text = bytes == null ? null : KnowledgeText.Extract(f.Name, bytes);
            if (text == null) { skipped++; continue; }
            if (known.Contains(f.Path)) updated++; else added++;
            AddDoc(idx, new KnowledgeDoc { Name = f.Name, Source = "folder", Path = f.Path, Ticks = f.Ticks, Size = f.Size }, text, o);
        }
        return new Stats(added, updated, removed, same, skipped);
    }

    public static bool AddUpload(KnowledgeIndex idx, string name, byte[] bytes, KnowledgeOptions o)
    {
        var text = KnowledgeText.Extract(name, bytes);
        if (text == null) return false;
        // the same name replaces the older upload
        var old = idx.Docs.FindIndex(d => d.Source == "upload" && d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (old >= 0) Remove(idx, new HashSet<int> { old });
        AddDoc(idx, new KnowledgeDoc { Name = name, Source = "upload", Ticks = DateTime.UtcNow.Ticks, Size = bytes.Length }, text, o);
        return true;
    }

    static void AddDoc(KnowledgeIndex idx, KnowledgeDoc doc, string text, KnowledgeOptions o)
    {
        idx.Docs.Add(doc);
        var d = idx.Docs.Count - 1;
        foreach (var c in KnowledgeText.Chunk(text, o.ChunkChars, o.Overlap))
            idx.Chunks.Add(new KnowledgeChunk { D = d, T = c });
    }

    public static void RemoveDoc(KnowledgeIndex idx, int docIndex) => Remove(idx, new HashSet<int> { docIndex });

    static void Remove(KnowledgeIndex idx, HashSet<int> drop)
    {
        if (drop.Count == 0) return;
        var map = new int[idx.Docs.Count];
        var docs = new List<KnowledgeDoc>();
        for (var i = 0; i < idx.Docs.Count; i++)
        {
            if (drop.Contains(i)) { map[i] = -1; continue; }
            map[i] = docs.Count;
            docs.Add(idx.Docs[i]);
        }
        idx.Chunks = idx.Chunks.Where(c => map[c.D] >= 0).Select(c => { c.D = map[c.D]; return c; }).ToList();
        idx.Docs = docs;
    }
}

/// <summary>Search over the passages: word matching (BM25, works for Persian and English) and, optionally, meaning (embeddings); the two rankings are merged.</summary>
public class KnowledgeSearcher
{
    readonly KnowledgeIndex _idx;
    readonly List<Dictionary<string, int>> _tf = new();
    readonly int[] _len;
    readonly Dictionary<string, int> _df = new();
    readonly double _avg;
    float[]?[]? _vecs;

    public KnowledgeIndex Index => _idx;

    public KnowledgeSearcher(KnowledgeIndex idx)
    {
        _idx = idx;
        _len = new int[idx.Chunks.Count];
        for (var i = 0; i < idx.Chunks.Count; i++)
        {
            var tf = new Dictionary<string, int>();
            var tokens = Tokenize(idx.Chunks[i].T);
            _len[i] = tokens.Count;
            foreach (var t in tokens) tf[t] = tf.TryGetValue(t, out var n) ? n + 1 : 1;
            foreach (var t in tf.Keys) _df[t] = _df.TryGetValue(t, out var n) ? n + 1 : 1;
            _tf.Add(tf);
        }
        _avg = _len.Length == 0 ? 1 : Math.Max(1, _len.Average());
    }

    static readonly Regex Word = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    public static List<string> Tokenize(string? text) =>
        Word.Matches(SearchText.Normalize(text).Text).Select(m => m.Value).Where(w => w.Length > 1 || char.IsDigit(w[0])).ToList();

    public List<(int Chunk, double Score)> Bm25(string query, int top)
    {
        var q = Tokenize(query).Distinct().ToList();
        var n = _idx.Chunks.Count;
        var res = new List<(int, double)>();
        if (q.Count == 0 || n == 0) return res;
        const double k1 = 1.5, b = 0.75;
        for (var i = 0; i < n; i++)
        {
            double score = 0;
            foreach (var t in q)
            {
                if (!_tf[i].TryGetValue(t, out var f)) continue;
                var df = _df[t];
                var idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
                score += idf * f * (k1 + 1) / (f + k1 * (1 - b + b * _len[i] / _avg));
            }
            if (score > 0) res.Add((i, score));
        }
        return res.OrderByDescending(x => x.Item2).Take(top).ToList();
    }

    public List<(int Chunk, double Score)> Semantic(float[] query, int top)
    {
        _vecs ??= _idx.Chunks.Select(c => c.V == null ? null : DecodeVec(c.V)).ToArray();
        var res = new List<(int, double)>();
        for (var i = 0; i < _vecs.Length; i++)
        {
            var v = _vecs[i];
            if (v == null || v.Length != query.Length) continue;
            var s = Cosine(query, v);
            if (s > 0) res.Add((i, s));
        }
        return res.OrderByDescending(x => x.Item2).Take(top).ToList();
    }

    public bool HasVectors => _idx.Chunks.Any(c => c.V != null);

    /// <summary>The best passages for a question. queryVec = embedding of the question (null = words only).</summary>
    public List<KnowledgeHit> Search(string query, int k, float[]? queryVec = null)
    {
        var pool = Math.Max(k * 4, 20);
        var words = Bm25(query, pool);
        List<(int Chunk, double Score)> ranked;
        if (queryVec != null && HasVectors)
            ranked = Fuse(words, Semantic(queryVec, pool));
        else
            ranked = words;
        return ranked.Take(k).Select(r => new KnowledgeHit(_idx.Docs[_idx.Chunks[r.Chunk].D].Name, _idx.Chunks[r.Chunk].T, r.Score)).ToList();
    }

    /// <summary>Reciprocal rank fusion of two rankings.</summary>
    public static List<(int Chunk, double Score)> Fuse(List<(int Chunk, double Score)> a, List<(int Chunk, double Score)> b)
    {
        var score = new Dictionary<int, double>();
        foreach (var list in new[] { a, b })
            for (var r = 0; r < list.Count; r++)
                score[list[r].Chunk] = score.GetValueOrDefault(list[r].Chunk) + 1.0 / (60 + r + 1);
        return score.OrderByDescending(x => x.Value).Select(x => (x.Key, x.Value)).ToList();
    }

    public static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    public static string EncodeVec(float[] v)
    {
        var bytes = new byte[v.Length * 4];
        Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
    }

    public static float[] DecodeVec(string s)
    {
        var bytes = Convert.FromBase64String(s);
        var v = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, v, 0, v.Length * 4);
        return v;
    }
}

public static class KnowledgePrompt
{
    /// <summary>The passages as a block for the system prompt, cut to maxChars.</summary>
    public static string Context(IReadOnlyList<KnowledgeHit> hits, int maxChars)
    {
        if (hits.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine("Passages from the project's documents. Use them when they are relevant to the user's question, and say when they do not contain the answer. Mention the source number like [1] when you rely on a passage.");
        var used = 0;
        for (var i = 0; i < hits.Count; i++)
        {
            var text = hits[i].Text;
            var room = maxChars - used;
            if (room < 200 && i > 0) break;
            if (text.Length > room) text = text[..Math.Max(200, room)] + "…";
            sb.AppendLine().Append('[').Append(i + 1).Append("] (").Append(hits[i].Doc).AppendLine(")").AppendLine(text);
            used += text.Length;
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Sources list added under the answer.</summary>
    public static string SourcesMarkdown(IReadOnlyList<KnowledgeHit> hits, string heading)
    {
        var sb = new StringBuilder("\n\n---\n**").Append(heading).AppendLine("**");
        for (var i = 0; i < hits.Count; i++)
        {
            var snippet = Regex.Replace(hits[i].Text, @"\s+", " ").Trim();
            if (snippet.Length > 110) snippet = snippet[..110] + "…";
            sb.AppendLine($"{i + 1}. **{hits[i].Doc.Replace("[", "(").Replace("]", ")")}** — {snippet.Replace("[", "(").Replace("]", ")")}");
        }
        return sb.ToString().TrimEnd();
    }
}

public static class KnowledgeEmbed
{
    public static JsonObject Body(string model, IEnumerable<string> inputs) =>
        new() { ["model"] = model, ["input"] = new JsonArray(inputs.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()) };

    /// <summary>The vectors of an /embeddings answer, in the order of the inputs.</summary>
    public static List<float[]> Parse(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("not an object");
        if (root["error"] != null) throw new InvalidOperationException(root["error"]!["message"]?.ToString() ?? root["error"]!.ToString());
        var data = root["data"] as JsonArray ?? throw new JsonException("no data");
        return data.OrderBy(d => (int?)d?["index"] ?? 0)
            .Select(d => d!["embedding"]!.AsArray().Select(x => (float)x!.GetValue<double>()).ToArray()).ToList();
    }
}

/// <summary>Read-only access to the files of ONE folder: nothing outside of it can be reached.</summary>
public static class FolderJail
{
    /// <summary>Full path of <paramref name="sub"/> inside <paramref name="root"/>; null when it would leave the folder.</summary>
    public static string? Resolve(string root, string? sub)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        var rootFull = Path.GetFullPath(root).TrimEnd('\\', '/');
        var s = (sub ?? "").Trim().Trim('"');
        if (s.Length > 0 && Path.IsPathRooted(s))
        {
            // an absolute path is accepted only when it is already inside the folder
            var abs = Path.GetFullPath(s);
            return Inside(rootFull, abs) ? abs : null;
        }
        var full = Path.GetFullPath(Path.Combine(rootFull, s));
        return Inside(rootFull, full) ? full : null;
    }

    static bool Inside(string rootFull, string full)
    {
        if (full.Equals(rootFull, StringComparison.OrdinalIgnoreCase)) return true;
        return full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               full.StartsWith(rootFull + "/", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Helpers for reading the other chats of a project.</summary>
public static class KnowledgeChats
{
    public static string MessageText(AiChat.Models.ChatMessage m)
    {
        var t = m.Content ?? "";
        if (m.Attachments.Count > 0) t += (t.Length > 0 ? "\n" : "") + "[attachments: " + string.Join(", ", m.Attachments.Select(a => a.Name)) + "]";
        return t.Trim();
    }

    /// <summary>Whole chat as plain text ("User:" / "Assistant:" lines).</summary>
    public static string FullText(AiChat.Models.Conversation c) =>
        string.Join("\n\n", c.Messages.Select(m => (m.Role == "user" ? "User: " : "Assistant: ") + MessageText(m)).Where(x => x.Length > 12));

    /// <summary>Messages from <paramref name="from"/> (1-based; negative = the last N) as text of at most <paramref name="limit"/> characters.</summary>
    public static (string Text, int First, int Last, int Total) Render(AiChat.Models.Conversation c, int from, int limit)
    {
        var msgs = c.Messages;
        var total = msgs.Count;
        var start = from < 0 ? Math.Max(0, total + from) : Math.Max(0, from - 1);
        var sb = new StringBuilder();
        var i = start;
        for (; i < total; i++)
        {
            var line = (msgs[i].Role == "user" ? "User: " : "Assistant: ") + MessageText(msgs[i]);
            if (sb.Length > 0 && sb.Length + line.Length > limit) break;
            if (line.Length > limit) line = line[..limit] + "…";
            sb.Append(line).Append("\n\n");
        }
        return (sb.ToString().TrimEnd(), start + 1, i, total);
    }

    /// <summary>The chat with this title (or id): exact title first, then a title that contains the words. Error text when none / several.</summary>
    public static (AiChat.Models.Conversation? Chat, string? Error) Find(IReadOnlyList<AiChat.Models.Conversation> chats, string query)
    {
        var q = SearchText.NormalizeQuery(query);
        if (q.Length == 0) return (null, "Give the title of the chat.");
        var byId = chats.FirstOrDefault(c => c.Id == query.Trim());
        if (byId != null) return (byId, null);
        var exact = chats.Where(c => SearchText.NormalizeQuery(c.Title) == q).OrderByDescending(c => c.Updated).FirstOrDefault();
        if (exact != null) return (exact, null);
        var part = chats.Where(c => SearchText.NormalizeQuery(c.Title).Contains(q)).OrderByDescending(c => c.Updated).ToList();
        if (part.Count == 1) return (part[0], null);
        if (part.Count > 1) return (null, "Several chats match: " + string.Join(" | ", part.Take(8).Select(c => c.Title)) + ". Use the exact title.");
        return (null, "No chat with this title in the project. Call list_project_chats to see the titles.");
    }

    /// <summary>A search index over the chats (document name = chat title).</summary>
    public static KnowledgeIndex BuildIndex(IEnumerable<AiChat.Models.Conversation> chats, KnowledgeOptions o)
    {
        var idx = new KnowledgeIndex();
        foreach (var c in chats)
        {
            var text = FullText(c);
            if (text.Length == 0) continue;
            idx.Docs.Add(new KnowledgeDoc { Name = "💬 " + c.Title, Source = "chat", Path = c.Id });
            var d = idx.Docs.Count - 1;
            foreach (var ch in KnowledgeText.Chunk(text, o.ChunkChars, o.Overlap)) idx.Chunks.Add(new KnowledgeChunk { D = d, T = ch });
        }
        return idx;
    }
}
