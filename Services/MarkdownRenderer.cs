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

    static string _tools = "🔧 ابزارها";

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

    public static void SetToolsLabel(string label)
    {
        _tools = label;
        LabelsVersion++;
    }

    // <think> blocks and groups of "> 🔧 ..." tool-call lines
    static readonly Regex Segments = new(@"<think>(?<t>.*?)(?:(?<c></think>)|\z)|(?<tool>(?:(?:^|(?<=</think>))> 🔧 [^\n]*(?:\n|\z)\s*)+)",
        RegexOptions.Singleline | RegexOptions.Multiline | RegexOptions.Compiled);

    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return "";
        var hasThink = markdown.Contains("<think>", StringComparison.Ordinal);
        var hasTools = markdown.Contains("> 🔧", StringComparison.Ordinal);
        if (!hasThink && !hasTools) return Render(markdown);

        var all = Segments.Matches(markdown).ToList();
        var thinks = all.Where(x => x.Groups["t"].Success).ToList();

        // all thinking of one answer (it comes back after every tool call) goes into ONE collapsible box
        var merged = string.Join("\n\n---\n\n", thinks.Select(x => x.Groups["t"].Value.Trim()).Where(x => x.Length > 0));
        var lastClosed = thinks.Count > 0 && thinks[^1].Groups["c"].Success;

        var toolMatches = all.Where(x => x.Groups["tool"].Success).ToList();
        var allCalls = toolMatches.SelectMany(x => x.Groups["tool"].Value.Split('\n'))
            .Select(l => l.Trim()).Where(l => l.StartsWith("> ", StringComparison.Ordinal))
            .Select(l => System.Net.WebUtility.HtmlEncode(l[2..].Trim())).ToList();
        // the box stays open while no answer text has started after the last call
        var toolsAnswered = false;
        if (toolMatches.Count > 0)
        {
            var last = toolMatches[^1];
            var tail = Think.Replace(markdown[(last.Index + last.Length)..], "");
            toolsAnswered = tail.Trim().Length > 0;
        }

        // a model that put its whole answer into the reasoning: the answer is the same text, so show it only once
        if (thinks.Count == 1 && merged.Length > 0 && lastClosed && toolMatches.Count == 0 &&
            Think.Replace(markdown, "").Trim() == merged)
            return Render(merged);

        var sb = new System.Text.StringBuilder();
        var pos = 0;
        var boxDone = false;
        var toolsDone = false;
        foreach (var m in all)
        {
            sb.Append(Render(markdown[pos..m.Index]));
            pos = m.Index + m.Length;

            if (m.Groups["t"].Success)
            {
                if (boxDone) continue;
                boxDone = true;
                var open = _thinkMode switch { "open" => true, "closed" => false, _ => !lastClosed };
                if (merged.Length > 0 && _thinkMode != "hidden")
                    sb.Append("<details class=\"think\"").Append(open ? " open" : "").Append("><summary>").Append(_think)
                      .Append("</summary><div class=\"think-body\" style=\"max-height:").Append(_thinkHeight).Append("px\">").Append(Render(merged)).Append("</div></details>");
            }
            else
            {
                // every tool call of the answer goes into ONE collapsible box (like the thinking box)
                if (toolsDone) continue;
                toolsDone = true;
                if (allCalls.Count == 0) continue;
                var open = _thinkMode switch { "open" => true, "closed" => false, _ => !toolsAnswered };
                var lines = string.Concat(allCalls.Select(c => "<div class=\"toolcall\" dir=\"auto\">" + c + "</div>"));
                sb.Append("<details class=\"think toolbox\"").Append(open ? " open" : "").Append("><summary>").Append(_tools)
                  .Append(" (").Append(allCalls.Count).Append(")</summary><div class=\"think-body toollist\" style=\"max-height:")
                  .Append(_thinkHeight).Append("px\">").Append(lines).Append("</div></details>");
            }
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
