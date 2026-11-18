using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiChat.Services;

/// <summary>A user-defined slash command: "/name text" is replaced by Prompt ({{text}} = the rest of the message).</summary>
public record Shortcut(string Name, string Prompt, string Description);

public static class ShortcutHelper
{
    public const string Sample = """
[
  { "name": "translate", "description": "Translate to English", "prompt": "Translate the following text to English. Reply with the translation only:\n\n{{text}}" },
  { "name": "fa", "description": "ترجمه به فارسی", "prompt": "متن زیر را به فارسی روان ترجمه کن:\n\n{{text}}" },
  { "name": "summary", "description": "Short summary", "prompt": "Summarize the following in 5 bullet points:\n\n{{text}}" },
  { "name": "fix", "description": "Fix grammar", "prompt": "Fix the grammar and spelling, keep the meaning and tone. Reply with the corrected text only:\n\n{{text}}" }
]
""";

    public static string ToJson(IEnumerable<Shortcut> list)
    {
        var arr = new JsonArray();
        foreach (var s in list)
            arr.Add(new JsonObject { ["name"] = s.Name, ["description"] = s.Description, ["prompt"] = s.Prompt });
        return arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>Extra ready-made prompts the library can add (names that already exist are skipped by the caller).</summary>
    public static List<Shortcut> MoreSamples() => new()
    {
        new("explain", "Explain simply", "Explain the following in simple terms with a short example:\n\n{{text}}"),
        new("code-review", "Review code", "Review this code. List bugs, risks and improvements, then show a corrected version:\n\n{{text}}"),
        new("email", "Polish an email", "Rewrite this as a clear, polite, professional email. Keep it short:\n\n{{text}}"),
        new("proscons", "Pros and cons", "List the pros and cons of the following, then give a one-line recommendation:\n\n{{text}}"),
        new("eli5", "ساده توضیح بده", "این موضوع را طوری توضیح بده که یک نوجوان بفهمد:\n\n{{text}}"),
        new("title", "Suggest titles", "Suggest 10 short, catchy titles for the following:\n\n{{text}}"),
    };

    public static List<Shortcut> Parse(string? json, out string? error)
    {
        error = null;
        var list = new List<Shortcut>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            if (JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is not JsonArray arr)
            {
                error = "The shortcuts must be a JSON array.";
                return list;
            }
            var i = 0;
            foreach (var it in arr)
            {
                i++;
                if (it is not JsonObject o) { error = $"Item {i} must be an object."; return new(); }
                var name = JPath.Text(o["name"]).Trim().TrimStart('/');
                var prompt = JPath.Text(o["prompt"]);
                if (name.Length == 0 || name.Any(char.IsWhiteSpace)) { error = $"Item {i}: \"name\" is required and cannot contain spaces."; return new(); }
                if (prompt.Length == 0) { error = $"Item {i}: \"prompt\" is required."; return new(); }
                if (list.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { error = $"Duplicate name: {name}"; return new(); }
                list.Add(new Shortcut(name, prompt, JPath.Text(o["description"])));
            }
        }
        catch (JsonException ex) { error = "Invalid JSON: " + ex.Message; return new(); }
        return list;
    }

    /// <summary>"/name rest" -> the shortcut's prompt; any other text is returned unchanged.</summary>
    public static string Expand(string text, IReadOnlyList<Shortcut> shortcuts)
    {
        if (shortcuts.Count == 0 || !text.StartsWith('/')) return text;
        var end = text.IndexOfAny(new[] { ' ', '\n', '\t' });
        var name = end < 0 ? text[1..] : text[1..end];
        var sc = shortcuts.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (sc == null) return text;
        var rest = end < 0 ? "" : text[(end + 1)..].Trim();
        if (sc.Prompt.Contains("{{text}}")) return sc.Prompt.Replace("{{text}}", rest).Trim();
        return rest.Length == 0 ? sc.Prompt : sc.Prompt + "\n\n" + rest;
    }
}
