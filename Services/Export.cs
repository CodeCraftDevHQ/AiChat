using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using AiChat.Models;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace AiChat.Services;

/// <summary>What goes into an exported chat (kept in the preferences as JSON).</summary>
public class ExportOptions
{
    /// <summary>md | docx | html | pdf | txt | json</summary>
    public string Format { get; set; } = "md";
    /// <summary>Include the thinking of the model.</summary>
    public bool Thinking { get; set; }
    /// <summary>Include the "tool call" lines.</summary>
    public bool Tools { get; set; }
    /// <summary>List the names of attached files (HTML also embeds the images).</summary>
    public bool Attachments { get; set; } = true;
    /// <summary>Write who spoke ("You" / the model) before every message.</summary>
    public bool Roles { get; set; } = true;
    public string UserLabel { get; set; } = "You";
    public string AssistantLabel { get; set; } = "Assistant";
}

/// <summary>Builds Markdown, text, HTML and Word (.docx) files from a chat. No MAUI code: unit-testable.</summary>
public static class ChatExport
{
    static readonly Regex ThinkBlock = new(@"<think>(.*?)(?:</think>|\z)", RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex Arabic = new(@"[؀-ۿݐ-ݿﭐ-﷿ﹰ-﻿]", RegexOptions.Compiled);

    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();

    public static bool HasRtl(string text) => Arabic.IsMatch(text);

    /// <summary>The text of one message after the options (thinking / tool lines) are applied.</summary>
    public static string BodyOf(ChatMessage m, ExportOptions o)
    {
        var text = m.Content ?? "";
        if (!o.Tools) text = ToolMarks.Strip(text);
        text = text.Contains("<think>", StringComparison.Ordinal) || text.Contains("</think>", StringComparison.Ordinal)
            ? (o.Thinking
                ? ThinkBlock.Replace(MarkdownRenderer.OpenStrayThink(text), x => Quote("💭 " + x.Groups[1].Value.Trim()) + "\n\n")
                : MarkdownRenderer.StripThink(text))
            : text;
        return text.Trim();
    }

    static string Quote(string text) => string.Join("\n", text.Split('\n').Select(l => "> " + l.TrimEnd('\r')));

    /// <param name="msgs">Messages in order. Image / file names are listed when <see cref="ExportOptions.Attachments"/> is on.</param>
    public static string BuildMarkdown(string title, IEnumerable<ChatMessage> msgs, ExportOptions o, bool withTitle = true)
    {
        var sb = new StringBuilder();
        if (withTitle && !string.IsNullOrWhiteSpace(title)) sb.Append("# ").Append(title.Trim()).Append("\n\n");
        var first = true;
        foreach (var m in msgs)
        {
            var body = BodyOf(m, o);
            var files = o.Attachments ? m.Attachments.Select(a => a.Name).Where(n => n.Length > 0).ToList() : new List<string>();
            if (body.Length == 0 && files.Count == 0) continue;
            if (!first) sb.Append("\n---\n\n");
            first = false;
            if (o.Roles)
            {
                var who = m.Role == "user" ? o.UserLabel : o.AssistantLabel + (string.IsNullOrWhiteSpace(m.Model) ? "" : " (" + m.Model + ")");
                sb.Append("## ").Append(m.Role == "user" ? "👤 " : "🤖 ").Append(who).Append("\n\n");
            }
            if (files.Count > 0) sb.Append(string.Join("  \n", files.Select(f => "📎 " + f))).Append("\n\n");
            if (body.Length > 0) sb.Append(body).Append("\n\n");
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    /// <summary>Plain text: the Markdown without its decoration.</summary>
    public static string BuildText(string title, IEnumerable<ChatMessage> msgs, ExportOptions o)
    {
        var md = BuildMarkdown(title, msgs, o);
        md = Regex.Replace(md, @"^#{1,6}\s*", "", RegexOptions.Multiline);
        md = Regex.Replace(md, @"^---\s*$", new string('-', 40), RegexOptions.Multiline);
        md = Regex.Replace(md, @"\*\*(.+?)\*\*", "$1");
        md = Regex.Replace(md, @"```[a-zA-Z0-9_+-]*\n", "");
        md = md.Replace("```", "");
        return md;
    }

    public static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    /// <summary>One self-contained HTML page (also what the PDF / print export uses). <paramref name="images"/> maps a file name to a data: URI.</summary>
    public static string BuildHtml(string title, string markdown, Dictionary<string, string>? images = null)
    {
        var body = Markdown.ToHtml(markdown, Pipeline);
        if (images != null)
            foreach (var (name, uri) in images)
                body = body.Replace("📎 " + Esc(name), "📎 " + Esc(name) + $"<br><img src=\"{uri}\" alt=\"{Esc(name)}\">");
        // every block aligns itself (Persian right, English left)
        body = Regex.Replace(body, @"<(p|ul|ol|blockquote|h[1-6]|table)(?=[\s>])", "<$1 dir=\"auto\"");
        var dir = HasRtl(markdown) ? "rtl" : "ltr";
        return "<!doctype html><html lang=\"" + (dir == "rtl" ? "fa" : "en") + "\" dir=\"" + dir + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>" +
               Esc(title) + "</title><style>" + Css + "</style></head><body><main>" + body + "</main></body></html>";
    }

    const string Css =
        "body{font-family:Tahoma,'Segoe UI',system-ui,sans-serif;line-height:1.8;color:#1b1b1b;background:#fff;margin:0}" +
        "main{max-width:820px;margin:0 auto;padding:28px 20px}h1{font-size:1.6em}h2{font-size:1.15em;margin-top:1.6em;color:#555}" +
        "pre{background:#f4f4f6;border:1px solid #e1e1e6;border-radius:8px;padding:12px;overflow:auto;direction:ltr;text-align:left;white-space:pre-wrap}" +
        "code{font-family:Consolas,'Cascadia Mono',monospace;font-size:.92em}:not(pre)>code{background:#f0f0f3;padding:1px 5px;border-radius:4px}" +
        "blockquote{margin:1em 0;padding:.2em 1em;border-inline-start:4px solid #cfd0d6;color:#555;background:#fafafa}" +
        "table{border-collapse:collapse;margin:1em 0}td,th{border:1px solid #d4d4da;padding:6px 10px}th{background:#f4f4f6}" +
        "hr{border:0;border-top:1px solid #ddd;margin:1.6em 0}img{max-width:100%;border-radius:8px}" +
        "@media print{main{max-width:none;padding:0}pre{page-break-inside:avoid}h2{page-break-after:avoid}}";

    // ------------------------------------------------------------------ Word (.docx)

    /// <summary>A minimal but valid .docx (paragraphs, headings, lists, code, quotes, tables, bold / italic, links).</summary>
    public static byte[] BuildDocx(string title, string markdown)
    {
        var doc = Markdown.Parse(markdown, Pipeline);
        var body = new StringBuilder();
        foreach (var b in doc) WriteBlock(b, body, 0, false);

        var xml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                  "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
                  body + "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1134\" w:right=\"1134\" w:bottom=\"1134\" w:left=\"1134\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>";

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            Add(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/></Types>");
            Add(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/></Relationships>");
            Add(zip, "docProps/core.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" " +
                "xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>" + Xml(title) + "</dc:title></cp:coreProperties>");
            Add(zip, "word/document.xml", xml);
        }
        return ms.ToArray();
    }

    static void Add(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = e.Open();
        var bytes = new UTF8Encoding(false).GetBytes(content);
        s.Write(bytes, 0, bytes.Length);
    }

    static string Xml(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') continue;   // not allowed in XML
            sb.Append(c switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", _ => c.ToString() });
        }
        return sb.ToString();
    }

    record Fmt(bool Bold = false, bool Italic = false, bool Code = false, bool Strike = false, int Size = 0, string? Color = null);

    static string Run(string text, Fmt f, bool rtl)
    {
        if (text.Length == 0) return "";
        var rp = new StringBuilder();
        if (f.Code) rp.Append("<w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\" w:cs=\"Consolas\"/>");
        else if (rtl) rp.Append("<w:rFonts w:cs=\"Tahoma\"/>");
        if (f.Bold) rp.Append("<w:b/><w:bCs/>");
        if (f.Italic) rp.Append("<w:i/><w:iCs/>");
        if (f.Strike) rp.Append("<w:strike/>");
        if (f.Color != null) rp.Append("<w:color w:val=\"").Append(f.Color).Append("\"/>");
        if (f.Size > 0) rp.Append("<w:sz w:val=\"").Append(f.Size).Append("\"/><w:szCs w:val=\"").Append(f.Size).Append("\"/>");
        if (rtl) rp.Append("<w:rtl/>");
        return "<w:r>" + (rp.Length > 0 ? "<w:rPr>" + rp + "</w:rPr>" : "") + "<w:t xml:space=\"preserve\">" + Xml(text) + "</w:t></w:r>";
    }

    static string Para(string runs, bool rtl, int indent = 0, string extra = "")
    {
        var pp = new StringBuilder();
        if (rtl) pp.Append("<w:bidi/>");
        pp.Append(extra);
        if (indent > 0) pp.Append("<w:ind w:").Append(rtl ? "right" : "left").Append("=\"").Append(indent).Append("\"/>");
        return "<w:p>" + (pp.Length > 0 ? "<w:pPr>" + pp + "</w:pPr>" : "") + runs + "</w:p>";
    }

    static void WriteBlock(Block b, StringBuilder sb, int indent, bool quote)
    {
        switch (b)
        {
            case HeadingBlock h:
            {
                var text = PlainText(h.Inline);
                var rtl = HasRtl(text);
                var size = h.Level switch { 1 => 40, 2 => 32, 3 => 28, _ => 24 };
                sb.Append(Para(Inlines(h.Inline, new Fmt(Bold: true, Size: size), rtl), rtl, indent, "<w:spacing w:before=\"240\" w:after=\"80\"/><w:keepNext/>"));
                break;
            }
            case ParagraphBlock p:
            {
                var text = PlainText(p.Inline);
                var rtl = HasRtl(text);
                sb.Append(Para(Inlines(p.Inline, quote ? new Fmt(Italic: true, Color: "555555") : new Fmt(), rtl), rtl, indent, "<w:spacing w:after=\"120\"/>"));
                break;
            }
            case CodeBlock c:
            {
                var lines = c.Lines.ToString().TrimEnd('\n', '\r').Split('\n');
                foreach (var line in lines)
                    sb.Append("<w:p><w:pPr><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F2F2F4\"/><w:spacing w:after=\"0\"/>")
                      .Append(indent > 0 ? "<w:ind w:left=\"" + indent + "\"/>" : "").Append("</w:pPr>")
                      .Append(Run(line.TrimEnd('\r').Replace("\t", "    "), new Fmt(Code: true, Size: 19), false)).Append("</w:p>");
                sb.Append("<w:p><w:pPr><w:spacing w:after=\"80\"/></w:pPr></w:p>");
                break;
            }
            case QuoteBlock q:
                foreach (var inner in q) WriteBlock(inner, sb, indent + 360, true);
                break;
            case ListBlock l:
            {
                var n = 0;
                if (l.IsOrdered && int.TryParse(l.OrderedStart, out var st)) n = st - 1;
                foreach (var item in l.OfType<ListItemBlock>())
                {
                    n++;
                    var marker = l.IsOrdered ? n + ". " : "• ";
                    var first = true;
                    foreach (var inner in item)
                    {
                        if (first && inner is ParagraphBlock ip)
                        {
                            var rtl = HasRtl(PlainText(ip.Inline));
                            sb.Append(Para(Run(marker, new Fmt(), rtl) + Inlines(ip.Inline, new Fmt(), rtl), rtl, indent + 360, "<w:spacing w:after=\"60\"/>"));
                        }
                        else WriteBlock(inner, sb, indent + 360, quote);
                        first = false;
                    }
                }
                break;
            }
            case Table t:
            {
                sb.Append("<w:tbl><w:tblPr><w:tblW w:w=\"5000\" w:type=\"pct\"/><w:tblBorders>");
                foreach (var side in new[] { "top", "left", "bottom", "right", "insideH", "insideV" })
                    sb.Append("<w:").Append(side).Append(" w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"BBBBBB\"/>");
                sb.Append("</w:tblBorders></w:tblPr>");
                foreach (var row in t.OfType<TableRow>())
                {
                    sb.Append("<w:tr>");
                    foreach (var cell in row.OfType<TableCell>())
                    {
                        sb.Append("<w:tc><w:tcPr>").Append(row.IsHeader ? "<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"EEEEF2\"/>" : "").Append("</w:tcPr>");
                        var any = false;
                        foreach (var inner in cell)
                        {
                            if (inner is ParagraphBlock cp)
                            {
                                var rtl = HasRtl(PlainText(cp.Inline));
                                sb.Append(Para(Inlines(cp.Inline, new Fmt(Bold: row.IsHeader), rtl), rtl));
                                any = true;
                            }
                            else { WriteBlock(inner, sb, 0, false); any = true; }
                        }
                        if (!any) sb.Append("<w:p/>");
                        sb.Append("</w:tc>");
                    }
                    sb.Append("</w:tr>");
                }
                sb.Append("</w:tbl><w:p><w:pPr><w:spacing w:after=\"80\"/></w:pPr></w:p>");
                break;
            }
            case ThematicBreakBlock:
                sb.Append("<w:p><w:pPr><w:pBdr><w:bottom w:val=\"single\" w:sz=\"6\" w:space=\"1\" w:color=\"CCCCCC\"/></w:pBdr></w:pPr></w:p>");
                break;
            case ContainerBlock cb:
                foreach (var inner in cb) WriteBlock(inner, sb, indent, quote);
                break;
            case LeafBlock lb when lb.Inline != null:
            {
                var rtl = HasRtl(PlainText(lb.Inline));
                sb.Append(Para(Inlines(lb.Inline, new Fmt(), rtl), rtl, indent));
                break;
            }
        }
    }

    static string PlainText(ContainerInline? inline)
    {
        if (inline == null) return "";
        var sb = new StringBuilder();
        foreach (var i in inline.Descendants())
        {
            if (i is LiteralInline lit) sb.Append(lit.Content.ToString());
            else if (i is CodeInline c) sb.Append(c.Content);
        }
        return sb.ToString();
    }

    static string Inlines(ContainerInline? inline, Fmt f, bool rtl)
    {
        if (inline == null) return "";
        var sb = new StringBuilder();
        foreach (var i in inline) sb.Append(InlineRun(i, f, rtl));
        return sb.ToString();
    }

    static string InlineRun(Inline i, Fmt f, bool rtl)
    {
        switch (i)
        {
            case LiteralInline lit: return Run(lit.Content.ToString(), f, rtl);
            case CodeInline code: return Run(code.Content, f with { Code = true }, false);
            case HtmlEntityInline ent: return Run(ent.Transcoded.ToString(), f, rtl);
            case LineBreakInline br: return br.IsHard ? "<w:r><w:br/></w:r>" : Run(" ", f, rtl);
            case EmphasisInline em:
            {
                var nf = em.DelimiterChar == '~' ? f with { Strike = true }
                    : em.DelimiterCount >= 2 ? f with { Bold = true } : f with { Italic = true };
                return Inlines(em, nf, rtl);
            }
            case LinkInline link when link.IsImage: return Run("[" + (PlainText(link).Length > 0 ? PlainText(link) : "image") + "]", f, rtl);
            case LinkInline link:
            {
                var label = Inlines(link, f with { Color = "1F5FBF" }, rtl);
                var url = link.Url ?? "";
                var plain = PlainText(link);
                return label + (url.Length > 0 && plain != url ? Run(" (" + url + ")", f with { Color = "777777", Size = 18 }, false) : "");
            }
            case AutolinkInline auto: return Run(auto.Url, f with { Color = "1F5FBF" }, false);
            case ContainerInline ci: return Inlines(ci, f, rtl);
            default: return "";
        }
    }
}
