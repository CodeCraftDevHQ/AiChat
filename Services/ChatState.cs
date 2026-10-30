using AiChat.Models;
using Microsoft.Maui.Storage;

namespace AiChat.Services;

/// <summary>
/// The single source of truth for the UI: providers, conversations, current selection and streaming.
/// Components subscribe to <see cref="Changed"/> and re-render.
/// </summary>
public class ChatState
{
    const string DefaultTitle = "گفتگوی جدید";

    readonly StorageService _storage;
    readonly ProviderFactory _factory;

    CancellationTokenSource? _cts;
    bool _initialized;
    DateTime _lastNotify = DateTime.MinValue;

    public ChatState(StorageService storage, ProviderFactory factory)
    {
        _storage = storage;
        _factory = factory;
    }

    public event Action? Changed;
    void Notify() => Changed?.Invoke();

    // ------------------------------------------------------------ state

    public List<ProviderConfig> Providers { get; private set; } = new();
    public List<Conversation> Conversations { get; private set; } = new();
    public Conversation? Active { get; private set; }

    public string? CurrentProviderId { get; private set; }
    public string CurrentModel { get; private set; } = "";
    public ProviderConfig? CurrentProvider => Providers.FirstOrDefault(p => p.Id == CurrentProviderId);

    // ------------------------------------------------------------ attachments

    public const int MaxAttachments = 6;
    const int MaxImageBytes = 5 * 1024 * 1024; // Claude's per-image limit
    const int MaxTextBytes = 512 * 1024;

    static readonly Dictionary<string, string> ImageMimes = new()
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp"
    };

    /// <summary>Files chosen for the next message.</summary>
    public List<Attachment> Pending { get; } = new();

    /// <summary>Adds a file to the next message. Returns an error text, or null on success.</summary>
    public async Task<string?> AddAttachmentAsync(string name, Stream stream)
    {
        if (Pending.Count >= MaxAttachments) return $"حداکثر {MaxAttachments} فایل در هر پیام مجاز است.";

        name = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(name)) name = "file";

        var ext = Path.GetExtension(name).ToLowerInvariant();
        var isImage = ImageMimes.TryGetValue(ext, out var mime);
        var limit = isImage ? MaxImageBytes : MaxTextBytes;

        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(buffer)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (ms.Length > limit)
                return $"«{name}» بزرگ‌تر از حد مجاز است (حداکثر {(isImage ? "5 مگابایت" : "512 کیلوبایت")}).";
        }
        var data = ms.ToArray();
        if (data.Length == 0) return $"«{name}» خالی است.";

        if (!isImage)
        {
            // anything with NUL bytes is binary (pdf, zip, docx, ...)
            var head = Math.Min(data.Length, 8192);
            for (var i = 0; i < head; i++)
                if (data[i] == 0)
                    return $"«{name}»: فقط عکس (png, jpg, gif, webp) و فایل‌های متنی/کد پشتیبانی می‌شوند.";
            mime = "text/plain";
        }

        var att = await _storage.SaveAttachmentAsync(name, mime!, isImage ? "image" : "text", data);
        Pending.Add(att);
        Notify();
        return null;
    }

    public void RemovePending(Attachment a)
    {
        if (Pending.Remove(a)) _storage.DeleteAttachments(new[] { a });
        Notify();
    }

    public Task<byte[]?> ReadAttachmentAsync(Attachment a) => _storage.ReadAttachmentAsync(a);

    public bool IsStreaming { get; private set; }
    public bool ShowSettings { get; private set; }
    public bool? SidebarOpen { get; private set; }
    public bool IsNarrow { get; private set; }
    public string? Notice { get; private set; }

    /// <summary>"system", "light" or "dark".</summary>
    public string Theme { get; private set; } = "system";

    public void SetTheme(string theme)
    {
        Theme = theme is "light" or "dark" ? theme : "system";
        Preferences.Default.Set("theme", Theme);
        Notify();
    }

    public bool IsRtl { get; private set; } = true;
    public string SystemPrompt { get; private set; } = "";

    // ------------------------------------------------------------ init

    public async Task InitAsync()
    {
        if (_initialized) return;
        _initialized = true;

        Providers = await _storage.LoadProvidersAsync();
        Conversations = (await _storage.LoadConversationsAsync() ?? new())
            .OrderByDescending(c => c.Updated).ToList();

        Theme = Preferences.Default.Get("theme", "system");
        IsRtl = Preferences.Default.Get("rtl", true);
        SystemPrompt = Preferences.Default.Get("system_prompt", "");

        var pid = Preferences.Default.Get("provider", "");
        var provider = Providers.FirstOrDefault(p => p.Id == pid) ?? Providers.FirstOrDefault();
        CurrentProviderId = provider?.Id;
        var model = Preferences.Default.Get("model", "");
        CurrentModel = !string.IsNullOrWhiteSpace(model) && provider != null && ModelsFor(provider).Contains(model)
            ? model
            : FirstModel(provider);

        Notify();
    }

    static string FirstModel(ProviderConfig? p) =>
        p == null ? "" : !string.IsNullOrWhiteSpace(p.DefaultModel) ? p.DefaultModel : p.Models.FirstOrDefault() ?? "";

    public List<string> ModelsFor(ProviderConfig? p)
    {
        var list = new List<string>();
        if (p == null) return list;
        if (!string.IsNullOrWhiteSpace(p.DefaultModel)) list.Add(p.DefaultModel);
        list.AddRange(p.Models);
        if (p.Id == CurrentProviderId && !string.IsNullOrWhiteSpace(CurrentModel)) list.Add(CurrentModel);
        return list.Distinct().ToList();
    }

    // ------------------------------------------------------------ UI flags

    public void SetNarrow(bool narrow)
    {
        IsNarrow = narrow;
        SidebarOpen = !narrow;
        Notify();
    }

    public void ToggleSidebar() { SidebarOpen = !(SidebarOpen ?? false); Notify(); }
    public void CloseSidebar() { SidebarOpen = false; Notify(); }

    public void OpenSettings() { ShowSettings = true; if (IsNarrow) SidebarOpen = false; Notify(); }
    public void CloseSettings() { ShowSettings = false; Notify(); }

    public void SetNotice(string? text) { Notice = text; Notify(); }
    public void ClearNotice() => SetNotice(null);

    public void SetRtl(bool rtl)
    {
        IsRtl = rtl;
        Preferences.Default.Set("rtl", rtl);
        Notify();
    }

    public void SetSystemPrompt(string text)
    {
        SystemPrompt = text;
        Preferences.Default.Set("system_prompt", text);
    }

    // ------------------------------------------------------------ provider selection

    public void SelectProvider(string? id)
    {
        var p = Providers.FirstOrDefault(x => x.Id == id);
        if (p == null) return;
        CurrentProviderId = p.Id;
        CurrentModel = FirstModel(p);
        PersistSelection();
        Notify();
    }

    public void SelectModel(string? model)
    {
        CurrentModel = model ?? "";
        PersistSelection();
        Notify();
    }

    void PersistSelection()
    {
        Preferences.Default.Set("provider", CurrentProviderId ?? "");
        Preferences.Default.Set("model", CurrentModel);
        if (Active != null)
        {
            Active.ProviderId = CurrentProviderId;
            Active.Model = CurrentModel;
            _ = _storage.SaveConversationsAsync(Conversations);
        }
    }

    // ------------------------------------------------------------ provider CRUD

    public async Task SaveProviderAsync(ProviderConfig cfg)
    {
        var copy = cfg.Clone();
        var idx = Providers.FindIndex(p => p.Id == copy.Id);
        if (idx >= 0) Providers[idx] = copy;
        else Providers.Add(copy);

        if (CurrentProvider == null)
        {
            CurrentProviderId = copy.Id;
            CurrentModel = FirstModel(copy);
        }
        else if (CurrentProviderId == copy.Id && string.IsNullOrWhiteSpace(CurrentModel))
        {
            CurrentModel = FirstModel(copy);
        }

        PersistSelection();
        Notify();
        await _storage.SaveProvidersAsync(Providers);
    }

    public async Task DeleteProviderAsync(string id)
    {
        Providers.RemoveAll(p => p.Id == id);
        _storage.DeleteSecret(id);

        if (CurrentProviderId == id)
        {
            var next = Providers.FirstOrDefault();
            CurrentProviderId = next?.Id;
            CurrentModel = FirstModel(next);
            PersistSelection();
        }

        Notify();
        await _storage.SaveProvidersAsync(Providers);
    }

    public Task<List<string>> FetchModelsAsync(ProviderConfig cfg, CancellationToken ct) =>
        _factory.Create(cfg.Type).ListModelsAsync(cfg, ct);

    // ------------------------------------------------------------ conversations

    public void NewChat()
    {
        Stop();
        Active = null;
        if (IsNarrow) SidebarOpen = false;
        Notify();
    }

    public void OpenConversation(Conversation c)
    {
        Stop();
        Active = c;

        var provider = Providers.FirstOrDefault(p => p.Id == c.ProviderId);
        if (provider != null)
        {
            CurrentProviderId = provider.Id;
            CurrentModel = string.IsNullOrWhiteSpace(c.Model) ? FirstModel(provider) : c.Model;
            Preferences.Default.Set("provider", CurrentProviderId);
            Preferences.Default.Set("model", CurrentModel);
        }

        if (IsNarrow) SidebarOpen = false;
        Notify();
    }

    public async Task RenameConversationAsync(Conversation c, string title)
    {
        title = (title ?? string.Empty).Trim();
        if (title.Length == 0 || title == c.Title) return;
        c.Title = title.Length > 80 ? title[..80] : title;
        Notify();
        await _storage.SaveConversationsAsync(Conversations);
    }

    public async Task DeleteConversationAsync(Conversation c)
    {
        if (Active == c) { Stop(); Active = null; }
        Conversations.Remove(c);
        _storage.DeleteAttachments(c.Messages.SelectMany(m => m.Attachments));
        Notify();
        await _storage.SaveConversationsAsync(Conversations);
    }

    public async Task ClearAllConversationsAsync()
    {
        Stop();
        Active = null;
        _storage.DeleteAttachments(Conversations.SelectMany(c => c.Messages).SelectMany(m => m.Attachments));
        _storage.DeleteAttachments(Pending);
        Pending.Clear();
        Conversations.Clear();
        Notify();
        await _storage.SaveConversationsAsync(Conversations);
    }

    // ------------------------------------------------------------ sending

    public string? ValidateSend()
    {
        if (CurrentProvider == null) return "ابتدا از بخش تنظیمات یک API اضافه کنید.";
        if (string.IsNullOrWhiteSpace(CurrentModel)) return "یک مدل انتخاب کنید (یا نام مدل را در تنظیمات وارد کنید).";
        return null;
    }

    public async Task SendAsync(string text)
    {
        text = text.Trim();
        if (IsStreaming || (text.Length == 0 && Pending.Count == 0)) return;

        var err = ValidateSend();
        if (err != null) { SetNotice(err); return; }

        var provider = CurrentProvider!;
        Notice = null;

        var attachments = Pending.ToList();
        Pending.Clear();

        if (Active == null)
        {
            Active = new Conversation { Title = MakeTitle(text.Length > 0 ? text : attachments[0].Name) };
            Conversations.Insert(0, Active);
        }

        var conv = Active;
        conv.ProviderId = provider.Id;
        conv.Model = CurrentModel;
        conv.Messages.Add(new ChatMessage { Role = "user", Content = text, Attachments = attachments });

        var reply = new ChatMessage { Role = "assistant", Model = $"{provider.Name} · {CurrentModel}" };
        conv.Messages.Add(reply);

        await RunAsync(conv, reply, provider, CurrentModel);
    }

    public async Task RegenerateAsync()
    {
        if (IsStreaming || Active == null) return;

        var err = ValidateSend();
        if (err != null) { SetNotice(err); return; }

        var conv = Active;
        if (conv.Messages.Count == 0 || conv.Messages[^1].Role != "assistant") return;
        conv.Messages.RemoveAt(conv.Messages.Count - 1);
        if (conv.Messages.Count == 0 || conv.Messages[^1].Role != "user") return;

        var provider = CurrentProvider!;
        conv.ProviderId = provider.Id;
        conv.Model = CurrentModel;

        var reply = new ChatMessage { Role = "assistant", Model = $"{provider.Name} · {CurrentModel}" };
        conv.Messages.Add(reply);
        Notice = null;

        await RunAsync(conv, reply, provider, CurrentModel);
    }

    public void Stop() => _cts?.Cancel();

    async Task RunAsync(Conversation conv, ChatMessage reply, ProviderConfig provider, string model)
    {
        IsStreaming = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Notify();

        var history = conv.Messages.TakeWhile(m => !ReferenceEquals(m, reply)).ToList();
        var impl = _factory.Create(provider.Type);

        try
        {
            await foreach (var chunk in impl.StreamAsync(provider, model, history, SystemPrompt, ct))
            {
                reply.Content += chunk;

                // throttle UI updates to ~20/s
                if ((DateTime.UtcNow - _lastNotify).TotalMilliseconds > 50)
                {
                    _lastNotify = DateTime.UtcNow;
                    Notify();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // user pressed stop
        }
        catch (Exception ex)
        {
            reply.ErrorText = ex.Message;
        }
        finally
        {
            IsStreaming = false;
            _cts?.Dispose();
            _cts = null;

            // stopped before any text arrived and no error -> drop the empty bubble
            if (string.IsNullOrEmpty(reply.Content) && reply.ErrorText == null)
                conv.Messages.Remove(reply);

            conv.Updated = DateTime.UtcNow;
            if (Conversations.Remove(conv)) Conversations.Insert(0, conv);

            Notify();
            await _storage.SaveConversationsAsync(Conversations);
        }
    }

    static string MakeTitle(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? DefaultTitle;
        return line.Length > 40 ? line[..40] + "…" : line;
    }
}
