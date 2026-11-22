using System.IO.Compression;
using System.Text;
using AiChat.Services;
using Xunit;

namespace AiChat.Tests;

public class KnowledgeTests
{
    static KnowledgeIndex Build(params (string name, string text)[] docs)
    {
        var idx = new KnowledgeIndex();
        var o = new KnowledgeOptions();
        foreach (var (name, text) in docs)
            Assert.True(KnowledgeIndexer.AddUpload(idx, name, Encoding.UTF8.GetBytes(text), o));
        return idx;
    }

    [Fact]
    public void Chunks_respect_size_and_keep_every_paragraph()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 60; i++) sb.Append($"Paragraph number {i} with some words to fill the line a little bit.\n\n");
        var chunks = KnowledgeText.Chunk(sb.ToString(), 500, 50);
        Assert.True(chunks.Count > 3);
        Assert.All(chunks, c => Assert.True(c.Length <= 500, "chunk too long: " + c.Length));
        for (var i = 0; i < 60; i++) Assert.Contains(chunks, c => c.Contains($"Paragraph number {i} "));
    }

    [Fact]
    public void A_single_huge_line_is_cut()
    {
        var chunks = KnowledgeText.Chunk(new string('x', 5000), 1000, 0);
        Assert.True(chunks.Count >= 5);
        Assert.All(chunks, c => Assert.True(c.Length <= 1000));
    }

    [Fact]
    public void Persian_words_are_found()
    {
        var idx = Build(("a.txt", "قیمت محصول در فروشگاه ۱۲۰۰ تومان است."), ("b.txt", "راهنمای نصب برنامه روی ویندوز و تنظیمات اولیه."));
        var s = new KnowledgeSearcher(idx);
        var hits = s.Search("نصب برنامه", 3);
        Assert.NotEmpty(hits);
        Assert.Equal("b.txt", hits[0].Doc);
    }

    [Fact]
    public void Digits_and_zwnj_are_normalized()
    {
        var idx = Build(("a.txt", "می‌خواهم مبلغ ۱۲۰۰ را بدانم"), ("b.txt", "something else entirely"));
        var s = new KnowledgeSearcher(idx);
        Assert.Equal("a.txt", s.Search("میخواهم 1200", 2)[0].Doc);
    }

    [Fact]
    public void English_words_rank_the_better_document_first()
    {
        var idx = Build(("cats.txt", "Cats sleep a lot. A cat likes milk and warm places."), ("dogs.txt", "Dogs bark. A dog likes walks and long games of fetch."));
        var s = new KnowledgeSearcher(idx);
        Assert.Equal("dogs.txt", s.Search("what does a dog like to play fetch", 2)[0].Doc);
    }

    [Fact]
    public void No_match_gives_no_hits()
    {
        var s = new KnowledgeSearcher(Build(("a.txt", "hello world")));
        Assert.Empty(s.Search("qqqqqq", 3));
        Assert.Empty(new KnowledgeSearcher(new KnowledgeIndex()).Search("anything", 3));
    }

    [Fact]
    public void Docx_text_is_read()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var e = zip.CreateEntry("word/document.xml");
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write("<?xml version=\"1.0\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
                    "<w:p><w:r><w:t>First paragraph</w:t></w:r></w:p><w:p><w:r><w:t>Second</w:t></w:r><w:r><w:t> one</w:t></w:r></w:p></w:body></w:document>");
        }
        var text = KnowledgeText.Extract("x.docx", ms.ToArray());
        Assert.NotNull(text);
        Assert.Contains("First paragraph", text);
        Assert.Contains("Second one", text);
        Assert.Null(KnowledgeText.Extract("broken.docx", new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void Binary_and_empty_files_are_refused()
    {
        Assert.Null(KnowledgeText.Extract("a.bin", new byte[] { 65, 0, 66, 0, 67 }));
        Assert.Null(KnowledgeText.Extract("a.txt", Encoding.UTF8.GetBytes("   \n ")));
    }

    [Fact]
    public void Html_tags_and_scripts_are_removed()
    {
        var t = KnowledgeText.Html("<html><style>p{color:red}</style><body><h1>Title</h1><p>Hello &amp; welcome</p><script>alert(1)</script></body></html>");
        Assert.Contains("Title", t);
        Assert.Contains("Hello & welcome", t);
        Assert.DoesNotContain("alert", t);
        Assert.DoesNotContain("color", t);
    }

    [Fact]
    public void Folder_sync_is_incremental()
    {
        var o = new KnowledgeOptions();
        var idx = new KnowledgeIndex();
        var files = new Dictionary<string, string> { ["/d/a.txt"] = "alpha text here", ["/d/b.txt"] = "beta text there" };
        byte[]? Read(string p) => files.TryGetValue(p, out var t) ? Encoding.UTF8.GetBytes(t) : null;

        var r1 = KnowledgeIndexer.SyncFolder(idx, new[] { new FoundFile("/d/a.txt", "a.txt", 1, 15), new FoundFile("/d/b.txt", "b.txt", 1, 15) }, Read, o);
        Assert.Equal(2, r1.Added);
        Assert.Equal(2, idx.Docs.Count);

        var r2 = KnowledgeIndexer.SyncFolder(idx, new[] { new FoundFile("/d/a.txt", "a.txt", 1, 15), new FoundFile("/d/b.txt", "b.txt", 1, 15) }, Read, o);
        Assert.Equal(0, r2.Added + r2.Updated + r2.Removed);
        Assert.Equal(2, r2.Unchanged);

        files["/d/a.txt"] = "alpha changed";
        var r3 = KnowledgeIndexer.SyncFolder(idx, new[] { new FoundFile("/d/a.txt", "a.txt", 2, 13) }, Read, o);
        Assert.Equal(1, r3.Updated);
        Assert.Equal(1, r3.Removed);
        Assert.Single(idx.Docs);
        Assert.All(idx.Chunks, c => Assert.Equal(0, c.D));
        Assert.Contains("changed", idx.Chunks[0].T);
    }

    [Fact]
    public void Removing_a_document_keeps_the_others_consistent()
    {
        var idx = Build(("a.txt", "aaa one"), ("b.txt", "bbb two"), ("c.txt", "ccc three"));
        KnowledgeIndexer.RemoveDoc(idx, 1);
        Assert.Equal(new[] { "a.txt", "c.txt" }, idx.Docs.Select(d => d.Name).ToArray());
        Assert.Equal("c.txt", idx.Docs[idx.Chunks.First(c => c.T.Contains("ccc")).D].Name);
        Assert.DoesNotContain(idx.Chunks, c => c.T.Contains("bbb"));
    }

    [Fact]
    public void Upload_with_the_same_name_replaces_the_old_one()
    {
        var idx = Build(("a.txt", "old words"));
        Assert.True(KnowledgeIndexer.AddUpload(idx, "A.txt", Encoding.UTF8.GetBytes("new words"), new KnowledgeOptions()));
        Assert.Single(idx.Docs);
        Assert.DoesNotContain(idx.Chunks, c => c.T.Contains("old"));
    }

    [Fact]
    public void Vectors_roundtrip_and_cosine_works()
    {
        var v = new[] { 0.5f, -1.25f, 3f };
        Assert.Equal(v, KnowledgeSearcher.DecodeVec(KnowledgeSearcher.EncodeVec(v)));
        Assert.Equal(1.0, KnowledgeSearcher.Cosine(new[] { 1f, 2f }, new[] { 2f, 4f }), 5);
        Assert.Equal(0.0, KnowledgeSearcher.Cosine(new[] { 1f, 0f }, new[] { 0f, 1f }), 5);
    }

    [Fact]
    public void Meaning_search_finds_what_words_cannot()
    {
        var idx = Build(("a.txt", "alpha"), ("b.txt", "beta"));
        idx.Chunks[0].V = KnowledgeSearcher.EncodeVec(new[] { 1f, 0f });
        idx.Chunks[1].V = KnowledgeSearcher.EncodeVec(new[] { 0f, 1f });
        var s = new KnowledgeSearcher(idx);
        Assert.True(s.HasVectors);
        var hits = s.Search("zzzz", 2, new[] { 0f, 1f });
        Assert.Single(hits);
        Assert.Equal("b.txt", hits[0].Doc);
    }

    [Fact]
    public void Fusion_puts_chunks_found_by_both_first()
    {
        var a = new List<(int Chunk, double Score)> { (1, 9), (2, 8), (3, 7) };
        var b = new List<(int Chunk, double Score)> { (3, 0.9), (4, 0.8) };
        var fused = KnowledgeSearcher.Fuse(a, b);
        Assert.Equal(3, fused[0].Chunk);   // found by both lists
        Assert.Equal(1, fused[1].Chunk);   // best rank of the first list only
        Assert.Equal(4, fused.Count);
    }

    [Fact]
    public void Embedding_answer_is_parsed_in_input_order()
    {
        var json = "{\"data\":[{\"index\":1,\"embedding\":[0.5,0.25]},{\"index\":0,\"embedding\":[1.5,2.5]}]}";
        var v = KnowledgeEmbed.Parse(json);
        Assert.Equal(2, v.Count);
        Assert.Equal(1.5f, v[0][0]);
        Assert.Equal(0.5f, v[1][0]);
        Assert.Throws<InvalidOperationException>(() => KnowledgeEmbed.Parse("{\"error\":{\"message\":\"bad key\"}}"));
        Assert.Equal("m", KnowledgeEmbed.Body("m", new[] { "x" })["model"]!.ToString());
    }

    [Fact]
    public void Prompt_numbers_the_sources_and_respects_the_limit()
    {
        var hits = new List<KnowledgeHit> { new("a.txt", new string('a', 500), 1), new("b.txt", new string('b', 500), 0.5) };
        var ctx = KnowledgePrompt.Context(hits, 2000);
        Assert.Contains("[1] (a.txt)", ctx);
        Assert.Contains("[2] (b.txt)", ctx);
        var small = KnowledgePrompt.Context(hits, 300);
        Assert.DoesNotContain("[2]", small);
        Assert.Equal("", KnowledgePrompt.Context(new List<KnowledgeHit>(), 1000));

        var md = KnowledgePrompt.SourcesMarkdown(hits, "Sources");
        Assert.Contains("**Sources**", md);
        Assert.Contains("1. **a.txt**", md);
    }

    [Fact]
    public void Options_survive_a_clone()
    {
        var o = new KnowledgeOptions { Mode = "tool", TopK = 9, Folder = "C:\\x", UseEmbeddings = true };
        var c = o.Clone();
        c.TopK = 1;
        Assert.Equal(9, o.TopK);
        Assert.Equal("tool", c.Mode);
        Assert.True(c.UseEmbeddings);
        Assert.Equal("auto", new KnowledgeOptions().Mode);
    }
}
