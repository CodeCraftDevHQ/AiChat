using System.Text.RegularExpressions;
using Markdig;

namespace AiChat.Services;

/// <summary>
/// Markdown -> HTML for assistant messages. Raw HTML in the model output is escaped (DisableHtml),
/// so a model can never inject script into the WebView.
/// </summary>
public static class MarkdownRenderer
{
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    // Every text block gets dir="auto" so Persian and English paragraphs each align correctly.
    static readonly Regex BlockTag = new(@"<(p|ul|ol|blockquote|h[1-6])(?=[\s>])", RegexOptions.Compiled);
    static readonly Regex PreWithLang = new(@"<pre><code class=""language-([^""]+)"">", RegexOptions.Compiled);
    static readonly Regex PreBare = new(@"<pre><code>", RegexOptions.Compiled);
    static readonly Regex BadHref = new(@"href=""\s*(javascript|data|vbscript):[^""]*""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    const string CopyButton = "<button type=\"button\" class=\"code-copy\">کپی</button>";

    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return "";

        var html = Markdown.ToHtml(markdown, Pipeline);
        html = BlockTag.Replace(html, "<$1 dir=\"auto\"");
        html = PreWithLang.Replace(html, "<pre data-lang=\"$1\">" + CopyButton + "<code class=\"language-$1\">");
        html = PreBare.Replace(html, "<pre data-lang=\"code\">" + CopyButton + "<code>");
        html = BadHref.Replace(html, "href=\"#\"");
        return html;
    }
}
