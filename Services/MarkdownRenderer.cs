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

    static readonly Regex ImgTag = new(@"<img ", RegexOptions.Compiled);

    static string _copy = "کپی", _save = "ذخیره", _copied = "کپی شد ✓", _failed = "ناموفق";

    /// <summary>Changes whenever the button labels change, so cached HTML can be rebuilt.</summary>
    public static int LabelsVersion { get; private set; }

    public static void SetLabels(string copy, string save, string copied, string failed)
    {
        _copy = copy; _save = save; _copied = copied; _failed = failed;
        LabelsVersion++;
    }

    static string CopyButton =>
        $"<button type=\"button\" class=\"code-save\">{_save}</button>" +
        $"<button type=\"button\" class=\"code-copy\" data-done=\"{_copied}\" data-fail=\"{_failed}\">{_copy}</button>";

    static string _think = "💭 فکر کردن مدل";

    static string _thinkMode = "auto";
    static int _thinkHeight = 320;

    /// <param name="mode">auto (open while thinking) | open | closed | hidden</param>
    public static void SetThinkOptions(string mode, int maxHeightPx)
    {
        _thinkMode = mode;
        _thinkHeight = maxHeightPx;
        LabelsVersion++;
    }

    public static void SetThinkLabel(string label)
    {
        _think = label;
        LabelsVersion++;
    }

    // <think>...</think> (also while it is still streaming, i.e. not closed yet)
    static readonly Regex Think = new(@"<think>(.*?)(?:</think>|\z)", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Removes the thinking blocks (used before the text is sent back to a model).</summary>
    public static string StripThink(string text) =>
        text.Contains("<think>", StringComparison.Ordinal) ? Think.Replace(text, "").TrimStart() : text;

    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return "";
        if (!markdown.Contains("<think>", StringComparison.Ordinal)) return Render(markdown);

        // thinking goes into a collapsible box (open while the model is still thinking)
        var sb = new System.Text.StringBuilder();
        var pos = 0;
        foreach (Match m in Think.Matches(markdown))
        {
            sb.Append(Render(markdown[pos..m.Index]));
            var closed = m.Value.EndsWith("</think>", StringComparison.Ordinal);
            var inner = m.Groups[1].Value.Trim();
            var open = _thinkMode switch { "open" => true, "closed" => false, _ => !closed };
            if (inner.Length > 0 && _thinkMode != "hidden")
                sb.Append("<details class=\"think\"").Append(open ? " open" : "").Append("><summary>").Append(_think)
                  .Append("</summary><div class=\"think-body\" style=\"max-height:").Append(_thinkHeight).Append("px\">").Append(Render(inner)).Append("</div></details>");
            pos = m.Index + m.Length;
        }
        sb.Append(Render(markdown[pos..]));
        return sb.ToString();
    }

    static string Render(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var html = Markdown.ToHtml(markdown, Pipeline);
        html = BlockTag.Replace(html, "<$1 dir=\"auto\"");
        html = PreWithLang.Replace(html, "<pre data-lang=\"$1\">" + CopyButton + "<code class=\"language-$1\">");
        html = PreBare.Replace(html, "<pre data-lang=\"code\">" + CopyButton + "<code>");
        html = BadHref.Replace(html, "href=\"#\"");
        html = ImgTag.Replace(html, "<img class=\"md-img\" loading=\"lazy\" ");
        return html;
    }
}
