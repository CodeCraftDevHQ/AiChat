using System.Text.Json.Serialization;
using AiChat.Services;

namespace AiChat.Models;

public enum ProviderType
{
    OpenAiCompatible,
    Anthropic,
    Gemini,
    FooocusApi, // image generator (Fooocus-API), not a chat model
    Custom // JSON-driven connector (image / video / audio / 3D / any HTTP API)
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

    // Advanced connection settings (all optional)
    /// <summary>"default" (by API type) | "bearer" | "header" | "query" | "none"</summary>
    public string AuthMode { get; set; } = "default";
    /// <summary>Header or query parameter name when AuthMode is "header" / "query".</summary>
    public string AuthName { get; set; } = "";
    /// <summary>Extra request headers, one "Name: value" per line ({apiKey} is replaced by the key).</summary>
    public string Headers { get; set; } = "";
    /// <summary>JSON object merged into every request body (a null value removes that field).</summary>
    public string ExtraBody { get; set; } = "";
    /// <summary>Replaces the chat endpoint path (Gemini: {model} is replaced by the model).</summary>
    public string ChatPath { get; set; } = "";
    /// <summary>Replaces the model-list endpoint path.</summary>
    public string ModelsPath { get; set; } = "";
    /// <summary>Settings controls (sliders, selects, ...) for normal APIs: a JSON array like connector "inputs".
    /// Their values can be used in ExtraBody as {{input.id}}.</summary>
    public string Controls { get; set; } = "";

    /// <summary>Custom connector: the JSON profile that describes the API.</summary>
    public string ConnectorJson { get; set; } = "";
    /// <summary>Custom connector: values of the profile's "inputs" (id -> value).</summary>
    public Dictionary<string, string> ConnectorInputs { get; set; } = new();

    [JsonIgnore]
    public bool IsChatConnector => Type == ProviderType.Custom &&
        System.Text.RegularExpressions.Regex.IsMatch(ConnectorJson ?? "", "\"kind\"\\s*:\\s*\"chat\"");

    [JsonIgnore]
    public bool IsGenerator => Type == ProviderType.FooocusApi || (Type == ProviderType.Custom && !IsChatConnector);

    /// <summary>OpenAI-compatible only: ask for one complete answer instead of a stream.</summary>
    public bool NoStream { get; set; }

    // Fooocus-API (image generation) options
    public string ImageAspect { get; set; } = "1152*896";
    public string ImagePerformance { get; set; } = "Speed";
    public int ImageCount { get; set; } = 1;
    public string ImageStyles { get; set; } = "Fooocus V2, Fooocus Enhance, Fooocus Sharp";
    public string NegativePrompt { get; set; } = "";

    /// <summary>Answer settings of this API (temperature, top_p, max tokens, extra system prompt).</summary>
    public GenSettings Gen { get; set; } = new();

    // The values actually used for one request (filled by the app from chat > API > global; never saved).
    [JsonIgnore] public double? Temperature { get; set; }
    [JsonIgnore] public double? TopP { get; set; }
    [JsonIgnore] public int? MaxTokens { get; set; }
    /// <summary>Tools offered to the model for this request (filled by the app; never saved).</summary>
    [JsonIgnore] public AiChat.Services.ToolSet? Tools { get; set; }
    /// <summary>OpenAI-compatible: allow tools (function calling) for this API when they are switched on in Settings.</summary>
    public bool UseTools { get; set; } = true;

    public ProviderConfig Clone() => new()
    {
        Gen = Gen.Copy(),
        Temperature = Temperature,
        TopP = TopP,
        MaxTokens = MaxTokens,
        Tools = Tools,
        UseTools = UseTools,
        Id = Id,
        Name = Name,
        Type = Type,
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
        DefaultModel = DefaultModel,
        Models = new List<string>(Models),
        AuthMode = AuthMode,
        AuthName = AuthName,
        Headers = Headers,
        ExtraBody = ExtraBody,
        ChatPath = ChatPath,
        ModelsPath = ModelsPath,
        NoStream = NoStream,
        Controls = Controls,
        ConnectorJson = ConnectorJson,
        ConnectorInputs = new Dictionary<string, string>(ConnectorInputs),
        ImageAspect = ImageAspect,
        ImagePerformance = ImagePerformance,
        ImageCount = ImageCount,
        ImageStyles = ImageStyles,
        NegativePrompt = NegativePrompt
    };
}

public class Attachment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "text"; // "image" | "text" | "pdf" | "video" | "audio" | "file"
    public string MimeType { get; set; } = "text/plain";
    public long Size { get; set; }

    /// <summary>
    /// Set after the model rejected images: the file stays visible in the chat but is no longer
    /// sent, so later text-only messages work again.
    /// </summary>
    public bool Excluded { get; set; }

    /// <summary>PDF only: "text" = send extracted text (default), "file" = send the original PDF (Claude / Gemini).</summary>
    public string PdfMode { get; set; } = "text";

    /// <summary>Generated images: the seed the generator reported (or that was sent), for re-use.</summary>
    public string? Seed { get; set; }

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

    /// <summary>Two-model chat: the answer of the second model (this message is the first model's answer).</summary>
    public ChatMessage? Pair { get; set; }
    /// <summary>Two-model chat: the user's verdict on the pair: "a" | "b" | "both" | "bad".</summary>
    public string? Vote { get; set; }

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

/// <summary>Optional answer settings. null / empty = not set (the next layer decides: chat > API > global > the service default).</summary>
public class GenSettings
{
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public int? MaxTokens { get; set; }
    /// <summary>Extra system instructions of this layer (added after the global ones).</summary>
    public string SystemPrompt { get; set; } = "";

    public GenSettings Copy() => new() { Temperature = Temperature, TopP = TopP, MaxTokens = MaxTokens, SystemPrompt = SystemPrompt };

    public bool IsEmpty => Temperature == null && TopP == null && MaxTokens == null && string.IsNullOrWhiteSpace(SystemPrompt);

    /// <summary>Later layers override earlier ones (numbers only; system prompts are joined by the caller).</summary>
    public static GenSettings Resolve(params GenSettings?[] layers)
    {
        var r = new GenSettings();
        foreach (var l in layers)
        {
            if (l == null) continue;
            r.Temperature = l.Temperature ?? r.Temperature;
            r.TopP = l.TopP ?? r.TopP;
            r.MaxTokens = l.MaxTokens ?? r.MaxTokens;
        }
        return r;
    }
}

/// <summary>A ready-made way to chat: system prompt + optional API/model + generation numbers + tools on/off.</summary>
public class Persona
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "🧑";
    /// <summary>Its SystemPrompt is the persona's instruction; the numbers override the API / global values.</summary>
    public GenSettings Gen { get; set; } = new();
    /// <summary>Optional: switch to this API (by id) when the persona is chosen.</summary>
    public string? ProviderId { get; set; }
    public string Model { get; set; } = "";
    /// <summary>null = follow the global setting, true / false = force tools on / off.</summary>
    public bool? Tools { get; set; }
    /// <summary>null = follow the global memory setting, true / false = force the long-term memory on / off.</summary>
    public bool? Memory { get; set; }

    public Persona Copy() => new()
    {
        Name = Name, Icon = Icon, Gen = Gen.Copy(), ProviderId = ProviderId, Model = Model, Tools = Tools, Memory = Memory
    };

    public Persona WithId(string id) { Id = id; return this; }

    public static List<Persona> Samples() => new()
    {
        new() { Name = "Programmer", Icon = "💻", Gen = new() { Temperature = 0.2, SystemPrompt = "You are a senior software engineer. Give correct, minimal, well-explained code. Ask a short question if the request is ambiguous. Prefer the user's language and frameworks." } },
        new() { Name = "مترجم", Icon = "🌍", Gen = new() { Temperature = 0.3, SystemPrompt = "تو یک مترجم حرفه‌ای هستی. هر متنی که می‌گیری را به زبان مقابل (فارسی ⇄ انگلیسی) ترجمه کن؛ فقط ترجمه را بنویس و لحن را حفظ کن." }, Tools = false },
        new() { Name = "Writer", Icon = "✍️", Gen = new() { Temperature = 1.0, SystemPrompt = "You are a creative writing partner. Write vivid, natural prose, offer alternatives, and keep the author's voice." }, Tools = false },
        new() { Name = "Researcher", Icon = "🔎", Gen = new() { Temperature = 0.3, SystemPrompt = "You are a careful researcher. For anything that may have changed recently, search the web first, compare several sources, and mention the sources and any uncertainty." }, Tools = true },
        new() { Name = "معلم", Icon = "🎓", Gen = new() { Temperature = 0.6, SystemPrompt = "تو یک معلم صبور هستی. مطلب را ساده و گام‌به‌گام با مثال توضیح بده و در پایان یک سؤال کوتاه برای سنجش بپرس." } },
    };
}

public class Conversation
{
    /// <summary>Two-model chat: the second API / model that answers every message together with the main one.</summary>
    public string? ArenaProviderId { get; set; }
    public string ArenaModel { get; set; } = "";

    /// <summary>The persona used in this chat (null = none).</summary>
    public string? PersonaId { get; set; }

    /// <summary>null = follow persona / global setting, true / false = long-term memory on / off in this chat.</summary>
    public bool? Memory { get; set; }

    /// <summary>Summary of the first <see cref="SummaryUpTo"/> messages (they are not sent any more; they stay visible).</summary>
    public string? Summary { get; set; }
    public int SummaryUpTo { get; set; }

    /// <summary>Settings of this chat only (null = none).</summary>
    public GenSettings? Gen { get; set; }

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

/// <summary>Result of testing one model with a tiny message (saved between app runs).</summary>
public record ModelTestResult(bool Ok, string? Error, bool RateLimited = false, DateTime? At = null);

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
        new("Fooocus-API (محلی)", ProviderType.FooocusApi, "http://127.0.0.1:8888"),
        new("اتصال سفارشی (JSON)", ProviderType.Custom, "http://127.0.0.1:8000"),
    };

    public static string BaseUrl(ProviderType type) => type switch
    {
        ProviderType.Anthropic => "https://api.anthropic.com/v1",
        ProviderType.Gemini => "https://generativelanguage.googleapis.com/v1beta",
        ProviderType.FooocusApi => "http://127.0.0.1:8888",
        ProviderType.Custom => "http://127.0.0.1:8000",
        _ => "https://api.openai.com/v1"
    };

    public static string TypeLabel(ProviderType type) => type switch
    {
        ProviderType.Anthropic => "Anthropic (Claude)",
        ProviderType.Gemini => "Google Gemini",
        ProviderType.FooocusApi => "Fooocus-API (تصویرساز)",
        ProviderType.Custom => "اتصال سفارشی (JSON)",
        _ => "سازگار با OpenAI"
    };
}
