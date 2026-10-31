using System.Text.Json.Serialization;
using AiChat.Services;

namespace AiChat.Models;

public enum ProviderType
{
    OpenAiCompatible,
    Anthropic,
    Gemini
}

public class ProviderConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public ProviderType Type { get; set; } = ProviderType.OpenAiCompatible;
    public string BaseUrl { get; set; } = "";

    // Stored in SecureStorage, never written to the JSON file.
    [JsonIgnore]
    public string ApiKey { get; set; } = "";

    public string DefaultModel { get; set; } = "";
    public List<string> Models { get; set; } = new();

    public ProviderConfig Clone() => new()
    {
        Id = Id,
        Name = Name,
        Type = Type,
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
        DefaultModel = DefaultModel,
        Models = new List<string>(Models)
    };
}

public class Attachment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "text"; // "image" | "text"
    public string MimeType { get; set; } = "text/plain";
    public long Size { get; set; }

    /// <summary>
    /// Set after the model rejected images: the file stays visible in the chat but is no longer
    /// sent, so later text-only messages work again.
    /// </summary>
    public bool Excluded { get; set; }

    /// <summary>File name inside the app's attachments folder.</summary>
    public string StoredName { get; set; } = "";

    /// <summary>Lazily loaded data: URI for showing images (never saved).</summary>
    [JsonIgnore]
    public string? DataUri { get; set; }

    [JsonIgnore]
    public string SizeText => Size >= 1024 * 1024 ? $"{Size / 1024d / 1024d:0.#} MB"
        : Size >= 1024 ? $"{Size / 1024d:0.#} KB" : $"{Size} B";
}

/// <summary>One alternative of an edited user message: its text plus everything that followed it.</summary>
public class MessageVersion
{
    public string Content { get; set; } = "";
    public List<Attachment> Attachments { get; set; } = new();
    public List<ChatMessage> Tail { get; set; } = new();
}

public class ChatMessage
{
    /// <summary>
    /// Set when a user message has been edited. The slot of the active version is kept empty
    /// (its data lives in the message and in Conversation.Messages); the other slots hold their snapshots.
    /// </summary>
    public List<MessageVersion>? Versions { get; set; }
    public int VersionIndex { get; set; }

    public string Role { get; set; } = "user"; // "user" | "assistant"
    public string Content { get; set; } = "";
    public List<Attachment> Attachments { get; set; } = new();
    public string? ErrorText { get; set; }
    public string? Model { get; set; } // "Provider · model" caption for assistant messages

    string? _htmlFor;
    string? _html;
    int _htmlLabels = -1;

    [JsonIgnore]
    public string Html
    {
        get
        {
            if (_html == null || _htmlFor != Content || _htmlLabels != MarkdownRenderer.LabelsVersion)
            {
                _html = MarkdownRenderer.ToHtml(Content);
                _htmlFor = Content;
                _htmlLabels = MarkdownRenderer.LabelsVersion;
            }
            return _html;
        }
    }
}

public class Conversation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "گفتگوی جدید";
    public string? ProviderId { get; set; }
    public string Model { get; set; } = "";
    public bool Pinned { get; set; }
    public string? ProjectId { get; set; }
    public DateTime Updated { get; set; } = DateTime.UtcNow;
    public List<ChatMessage> Messages { get; set; } = new();
}

public class Project
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary>Added to the system prompt of every conversation in this project.</summary>
    public string Instructions { get; set; } = "";

    /// <summary>Reference files that are sent with the first message of every conversation in this project.</summary>
    public List<Attachment> Files { get; set; } = new();

    public Project Clone() => new()
    {
        Id = Id,
        Name = Name,
        Instructions = Instructions,
        Files = new List<Attachment>(Files)
    };
}

public record Preset(string Name, ProviderType Type, string BaseUrl);

public static class ProviderDefaults
{
    public static readonly Preset[] Presets =
    {
        new("OpenAI", ProviderType.OpenAiCompatible, "https://api.openai.com/v1"),
        new("Anthropic (Claude)", ProviderType.Anthropic, "https://api.anthropic.com/v1"),
        new("Google Gemini", ProviderType.Gemini, "https://generativelanguage.googleapis.com/v1beta"),
        new("OpenRouter", ProviderType.OpenAiCompatible, "https://openrouter.ai/api/v1"),
        new("DeepSeek", ProviderType.OpenAiCompatible, "https://api.deepseek.com/v1"),
        new("Groq", ProviderType.OpenAiCompatible, "https://api.groq.com/openai/v1"),
        new("Mistral", ProviderType.OpenAiCompatible, "https://api.mistral.ai/v1"),
        new("xAI (Grok)", ProviderType.OpenAiCompatible, "https://api.x.ai/v1"),
        new("Together AI", ProviderType.OpenAiCompatible, "https://api.together.xyz/v1"),
        new("Ollama (محلی)", ProviderType.OpenAiCompatible, "http://localhost:11434/v1"),
        new("LM Studio (محلی)", ProviderType.OpenAiCompatible, "http://localhost:1234/v1"),
    };

    public static string BaseUrl(ProviderType type) => type switch
    {
        ProviderType.Anthropic => "https://api.anthropic.com/v1",
        ProviderType.Gemini => "https://generativelanguage.googleapis.com/v1beta",
        _ => "https://api.openai.com/v1"
    };

    public static string TypeLabel(ProviderType type) => type switch
    {
        ProviderType.Anthropic => "Anthropic (Claude)",
        ProviderType.Gemini => "Google Gemini",
        _ => "سازگار با OpenAI"
    };
}
