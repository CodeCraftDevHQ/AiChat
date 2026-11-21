using System.IO.Compression;
using AiChat.Models;
using AiChat.Services;

namespace AiChat.Tests;

public class ExportTests
{
    static List<ChatMessage> Chat() => new()
    {
        new() { Role = "user", Content = "Hello", Attachments = { new Attachment { Name = "pic.png", Kind = "image" } } },
        new() { Role = "assistant", Model = "P · m", Content = "<think>secret thoughts</think>\n\n> 🔧 Web search: x\n\nThe **answer**\n\n```cs\nvar a = 1;\n```" }
    };

    [Fact]
    public void Markdown_hides_thinking_and_tools_by_default()
    {
        var md = ChatExport.BuildMarkdown("T", Chat(), new ExportOptions());
        Assert.Contains("# T", md);
        Assert.Contains("👤 You", md);
        Assert.Contains("🤖 Assistant (P · m)", md);
        Assert.Contains("📎 pic.png", md);
        Assert.Contains("The **answer**", md);
        Assert.DoesNotContain("secret", md);
        Assert.DoesNotContain("🔧", md);
    }

    [Fact]
    public void Markdown_can_include_thinking_and_tools()
    {
        var md = ChatExport.BuildMarkdown("T", Chat(), new ExportOptions { Thinking = true, Tools = true, Roles = false, Attachments = false });
        Assert.Contains("> 💭 secret thoughts", md);
        Assert.Contains("🔧 Web search", md);
        Assert.DoesNotContain("👤", md);
        Assert.DoesNotContain("pic.png", md);
    }

    [Fact]
    public void Stray_closing_think_tag_is_handled()
    {
        var m = new ChatMessage { Role = "assistant", Content = "thought</think>\n\nAnswer" };
        Assert.Equal("Answer", ChatExport.BodyOf(m, new ExportOptions()));
    }

    [Fact]
    public void Text_has_no_markdown_marks()
    {
        var t = ChatExport.BuildText("T", Chat(), new ExportOptions());
        Assert.DoesNotContain("**", t);
        Assert.DoesNotContain("```", t);
        Assert.Contains("The answer", t);
    }

    [Fact]
    public void Html_is_a_page_with_direction_and_embedded_images()
    {
        var html = ChatExport.BuildHtml("عنوان <x>", "سلام\n\n📎 pic.png", new Dictionary<string, string> { ["pic.png"] = "data:image/png;base64,AAAA" });
        Assert.Contains("dir=\"rtl\"", html);
        Assert.Contains("&lt;x&gt;", html);
        Assert.Contains("data:image/png;base64,AAAA", html);
        Assert.StartsWith("<!doctype html>", html);
        Assert.DoesNotContain("<script", html);
    }

    [Fact]
    public void Docx_is_a_valid_zip_with_the_text()
    {
        var md = "# Title\n\nHello **bold** and *it* `code`\n\n- one\n- two\n\n1. a\n2. b\n\n> quote\n\n```\nline1\nline2\n```\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n[link](https://x.test)\n\nسلام دنیا";
        var bytes = ChatExport.BuildDocx("T", md);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        Assert.NotNull(zip.GetEntry("_rels/.rels"));
        var xml = new StreamReader(zip.GetEntry("word/document.xml")!.Open()).ReadToEnd();
        var doc = System.Xml.Linq.XDocument.Parse(xml);   // well-formed
        Assert.NotNull(doc.Root);
        foreach (var text in new[] { "Title", "Hello ", "bold", "one", "two", "line1", "line2", "quote", "https://x.test", "سلام دنیا" })
            Assert.Contains(text, xml);
        Assert.Contains("<w:tbl>", xml);
        Assert.Contains("<w:bidi/>", xml);          // the Persian paragraph
        Assert.Contains("<w:b/>", xml);
    }

    [Fact]
    public void Docx_escapes_xml_characters_and_survives_odd_input()
    {
        var bytes = ChatExport.BuildDocx("a&b", "5 < 6 & 7 > 3 \u0001 text\n\n<script>x</script>");
        using var zip = new ZipArchive(new MemoryStream(bytes));
        var xml = new StreamReader(zip.GetEntry("word/document.xml")!.Open()).ReadToEnd();
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        Assert.DoesNotContain("\u0001", xml);
        System.Xml.Linq.XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var text = string.Concat(doc.Descendants(w + "t").Select(t => t.Value));
        Assert.Contains("5 < 6 & 7 > 3", text);
    }
}
