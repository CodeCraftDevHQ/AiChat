using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AiChat.Services;

/// <summary>Settings of the voice features (read aloud / speech to text). Everything is off by default.</summary>
public class VoiceOptions
{
    /// <summary>Master switch: nothing voice-related is shown or used while this is off.</summary>
    public bool Enabled { get; set; }

    // ---------------- read aloud
    /// <summary>Show a 🔊 button on every answer.</summary>
    public bool ReadButton { get; set; } = true;
    /// <summary>Read every new answer aloud when it is finished.</summary>
    public bool AutoRead { get; set; }
    /// <summary>"device" = the voices of the device (free, offline) | "api" = an OpenAI-compatible /audio/speech API.</summary>
    public string TtsMode { get; set; } = "device";
    public bool SkipCode { get; set; } = true;
    public bool SkipThinking { get; set; } = true;
    public bool SkipLinks { get; set; } = true;
    public bool SkipToolLines { get; set; } = true;

    // device voice
    /// <summary>Language / voice name of the device voice (empty = chosen by the language of the text).</summary>
    public string DeviceLocale { get; set; } = "";
    public double Pitch { get; set; } = 1.0;
    public double Volume { get; set; } = 1.0;

    // api voice
    /// <summary>Empty = a separate address and key below; otherwise the address and key of one of the app's APIs.</summary>
    public string TtsProviderId { get; set; } = "";
    public string TtsBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string TtsPath { get; set; } = "/audio/speech";
    public string TtsModel { get; set; } = "tts-1";
    public string TtsVoice { get; set; } = "alloy";
    public string TtsFormat { get; set; } = "mp3";
    public double TtsSpeed { get; set; } = 1.0;
    /// <summary>JSON object merged into the request (a null value removes a field).</summary>
    public string TtsExtraBody { get; set; } = "";
    /// <summary>Longest text sent in one request (characters).</summary>
    public int TtsChunk { get; set; } = 3000;
    [JsonIgnore] public string TtsKey { get; set; } = "";

    // ---------------- speech to text
    public bool SttEnabled { get; set; }
    public string SttProviderId { get; set; } = "";
    public string SttBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string SttPath { get; set; } = "/audio/transcriptions";
    public string SttModel { get; set; } = "whisper-1";
    /// <summary>ISO code like "fa" or "en" (empty = detected by the model).</summary>
    public string SttLanguage { get; set; } = "";
    public string SttPrompt { get; set; } = "";
    /// <summary>Send the message right after the text is written.</summary>
    public bool AutoSend { get; set; }
    [JsonIgnore] public string SttKey { get; set; } = "";
}

/// <summary>Turns an answer (Markdown) into text that sounds right when read aloud.</summary>
public static class SpeechText
{
    static readonly Regex Think = new(@"<think>.*?(</think>|$)", RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex CodeBlock = new(@"```.*?(```|$)", RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex ToolLine = new(@"^\s*>\s*🔧.*$", RegexOptions.Multiline | RegexOptions.Compiled);
    static readonly Regex Image = new(@"!\[[^\]]*\]\([^)]*\)", RegexOptions.Compiled);
    static readonly Regex Link = new(@"\[([^\]]*)\]\(([^)]*)\)", RegexOptions.Compiled);
    static readonly Regex BareUrl = new(@"https?://\S+", RegexOptions.Compiled);
    static readonly Regex Html = new(@"<[^>]+>", RegexOptions.Compiled);
    static readonly Regex Heading = new(@"^\s{0,3}#{1,6}\s*", RegexOptions.Multiline | RegexOptions.Compiled);
    static readonly Regex Bullet = new(@"^\s*([-*+]|\d+[.)])\s+", RegexOptions.Multiline | RegexOptions.Compiled);
    static readonly Regex Quote = new(@"^\s*>\s?", RegexOptions.Multiline | RegexOptions.Compiled);
    static readonly Regex Rule = new(@"^\s*([-*_]\s*){3,}$", RegexOptions.Multiline | RegexOptions.Compiled);
    static readonly Regex TableSep = new(@"^\s*\|?[\s:|-]+\|[\s:|-]*$", RegexOptions.Multiline | RegexOptions.Compiled);
    static readonly Regex Emph = new(@"(\*\*|__|~~|\*|`)", RegexOptions.Compiled);
    static readonly Regex Blank = new(@"\n{3,}", RegexOptions.Compiled);

    public static string Clean(string? markdown, VoiceOptions o)
    {
        var t = markdown ?? "";
        if (o.SkipThinking) t = Think.Replace(t, "");
        else t = t.Replace("<think>", "").Replace("</think>", "");
        t = CodeBlock.Replace(t, m =>
        {
            if (o.SkipCode) return "";
            var inner = m.Value.TrimStart('`');
            var nl = inner.IndexOf('\n');
            inner = nl >= 0 ? inner[(nl + 1)..] : "";   // drop the language word after the opening fence
            return "\n" + inner.TrimEnd('`').Trim() + "\n";
        });
        if (o.SkipToolLines) t = ToolLine.Replace(t, "");
        t = Image.Replace(t, "");
        t = o.SkipLinks ? Link.Replace(t, "$1") : Link.Replace(t, "$1 ($2)");
        if (o.SkipLinks) t = BareUrl.Replace(t, "");
        t = Html.Replace(t, "");
        t = Heading.Replace(t, "");
        t = TableSep.Replace(t, "");
        t = Rule.Replace(t, "");
        t = Quote.Replace(t, "");
        t = Bullet.Replace(t, "");
        t = t.Replace("|", ", ");
        t = Emph.Replace(t, "");
        t = Blank.Replace(t, "\n\n");
        return t.Trim();
    }

    /// <summary>Splits text into pieces of at most <paramref name="max"/> characters, at paragraph / sentence ends when possible.</summary>
    public static List<string> Chunks(string text, int max)
    {
        max = Math.Max(50, max);
        var result = new List<string>();
        var rest = text.Trim();
        while (rest.Length > max)
        {
            var cut = -1;
            foreach (var seps in new[] { new[] { "\n\n" }, new[] { "\n" }, new[] { ". ", "؟ ", "? ", "! ", "。", "؛ ", "; " }, new[] { "، ", ", ", " " } })
            {
                foreach (var sep in seps)
                {
                    var i = rest.LastIndexOf(sep, max - 1, StringComparison.Ordinal);
                    if (i > max / 3) cut = Math.Max(cut, i + sep.Length);
                }
                if (cut > 0) break;
            }
            if (cut <= 0) cut = max;
            result.Add(rest[..cut].Trim());
            rest = rest[cut..].Trim();
        }
        if (rest.Length > 0) result.Add(rest);
        return result.Where(x => x.Length > 0).ToList();
    }

    /// <summary>"fa" when most letters are Persian / Arabic, otherwise "en".</summary>
    public static string GuessLanguage(string text)
    {
        int arabic = 0, latin = 0;
        foreach (var ch in text)
        {
            if (ch >= 0x0600 && ch <= 0x06FF || ch >= 0xFB50 && ch <= 0xFDFF || ch >= 0xFE70 && ch <= 0xFEFF) arabic++;
            else if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z') latin++;
        }
        return arabic > latin ? "fa" : "en";
    }
}

/// <summary>Requests of the OpenAI-compatible speech APIs.</summary>
public static class VoiceApi
{
    public static string Join(string baseUrl, string path) =>
        (baseUrl ?? "").Trim().TrimEnd('/') + "/" + (path ?? "").Trim().TrimStart('/');

    public static string MimeFor(string format) => (format ?? "").Trim().ToLowerInvariant() switch
    {
        "wav" => "audio/wav",
        "opus" => "audio/ogg",
        "ogg" => "audio/ogg",
        "aac" => "audio/aac",
        "flac" => "audio/flac",
        "pcm" => "audio/pcm",
        _ => "audio/mpeg"
    };

    public static JsonObject BuildSpeechBody(VoiceOptions o, string text)
    {
        var body = new JsonObject
        {
            ["model"] = o.TtsModel,
            ["input"] = text,
            ["voice"] = o.TtsVoice,
            ["response_format"] = string.IsNullOrWhiteSpace(o.TtsFormat) ? "mp3" : o.TtsFormat
        };
        if (Math.Abs(o.TtsSpeed - 1.0) > 0.001) body["speed"] = o.TtsSpeed;
        if (!string.IsNullOrWhiteSpace(o.TtsExtraBody))
        {
            try
            {
                if (JsonNode.Parse(o.TtsExtraBody) is JsonObject extra)
                    foreach (var (k, v) in extra.ToList())
                        body[k] = v?.DeepClone();
            }
            catch (JsonException) { /* ignore a broken extra body */ }
        }
        return body;
    }

    /// <summary>The text of a transcription response ({"text": "..."}; a plain-text answer is accepted too).</summary>
    public static string ParseTranscript(string response)
    {
        var r = (response ?? "").Trim();
        if (r.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(r);
                if (doc.RootElement.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) return t.GetString()!.Trim();
                if (doc.RootElement.TryGetProperty("error", out var e)) throw new InvalidOperationException(e.ToString());
            }
            catch (JsonException) { }
        }
        return r.StartsWith('{') ? "" : r;
    }

    public static string FileNameFor(string mime)
    {
        var m = (mime ?? "").ToLowerInvariant();
        var ext = m.Contains("webm") ? "webm" : m.Contains("ogg") ? "ogg" : m.Contains("mp4") || m.Contains("m4a") ? "mp4"
            : m.Contains("wav") ? "wav" : m.Contains("mpeg") || m.Contains("mp3") ? "mp3" : "webm";
        return "audio." + ext;
    }
}
