using AiChat.Models;
using Microsoft.Maui.Storage;

namespace AiChat.Services;

/// <summary>
/// The single source of truth for the UI: providers, conversations, current selection and streaming.
/// Components subscribe to <see cref="Changed"/> and re-render.
/// </summary>
public class ChatState
{
    string DefaultTitle => L["گفتگوی جدید"];

    readonly StorageService _storage;
    readonly ProviderFactory _factory;
    readonly FileExportService _export;
    readonly HttpClient _http;
    readonly LogService _log;
    public Loc L { get; }

    CancellationTokenSource? _cts;
    bool _initialized;
    DateTime _lastNotify = DateTime.MinValue;

    public ChatState(StorageService storage, ProviderFactory factory, FileExportService export, HttpClient http, LogService log, Loc loc)
    {
        _storage = storage;
        _factory = factory;
        _export = export;
        _http = http;
        _log = log;
        L = loc;
    }

    // ------------------------------------------------------------ viewer, saving, toast

    /// <summary>An image shown full-size in the viewer.</summary>
    public record ViewerImage(string Src, string Name, Attachment? Att);

    // ------------------------------------------------------------ sidebar search (all chats)

    public record SearchHit(Conversation Conv, int Count, bool TitleMatch, string? Before, string? Match, string? After);

    /// <summary>true = search inside the messages of every chat, false = chat names only.</summary>
    public bool SidebarSearchInContent { get; private set; } = Preferences.Default.Get("sidebar_search_content", true);

    public void SetSidebarSearchInContent(bool value)
    {
        SidebarSearchInContent = value;
        Preferences.Default.Set("sidebar_search_content", value);
        Notify();
    }

    public List<SearchHit> SearchConversations(string query, bool inContent)
    {
        var q = SearchText.NormalizeQuery(query);
        var hits = new List<SearchHit>();
        if (q.Length == 0) return hits;

        foreach (var c in Conversations)
        {
            var titleMatch = SearchText.Count(c.Title, q) > 0;
            var count = 0;
            (string, string, string)? snippet = null;

            if (inContent)
            {
                foreach (var m in c.Messages)
                {
                    var n = SearchText.Count(m.Content, q);
                    if (n == 0) continue;
                    count += n;
                    snippet ??= SearchText.Snippet(m.Content, q);
                }
            }

            if (!titleMatch && count == 0) continue;
            hits.Add(new SearchHit(c, count, titleMatch, snippet?.Item1, snippet?.Item2, snippet?.Item3));
        }
        return hits;
    }

    /// <summary>Text to put in the in-chat search bar when a chat is opened from a search result.</summary>
    public string? PendingSearch { get; set; }

    public void OpenConversationWithSearch(Conversation c, string query)
    {
        OpenConversation(c);
        PendingSearch = query;
        SearchOpen = true;
        Notify();
    }

    /// <summary>The "search in this chat" bar.</summary>
    public bool SearchOpen { get; private set; }
    public void OpenSearch() { SearchOpen = true; Notify(); }
    public void CloseSearch() { SearchOpen = false; Notify(); }

    public bool ShowLogs { get; private set; }
    public void OpenLogs() { ShowLogs = true; Notify(); }
    public void CloseLogs() { ShowLogs = false; Notify(); }

    public ViewerImage? Viewer { get; private set; }
    public string? Toast { get; private set; }

    public void OpenViewer(string src, string name, Attachment? att = null)
    {
        Viewer = new ViewerImage(src, name, att);
        Notify();
    }

    public void CloseViewer()
    {
        Viewer = null;
        Notify();
    }

    async void ShowToast(string text)
    {
        Toast = text;
        Notify();
        await Task.Delay(2500);
        if (Toast == text) { Toast = null; Notify(); }
    }

    async Task SaveBytesAsync(string name, byte[] data)
    {
        try
        {
            var where = await _export.SaveAsync(name, data);
            if (where != null) ShowToast(L["ذخیره شد ✓"]);
        }
        catch (Exception ex)
        {
            _log.Error($"Saving file '{name}' failed", ex);
            SetNotice(L["ذخیره ناموفق بود: "] + ex.Message);
        }
    }

    public Task SaveTextAsync(string name, string text) =>
        SaveBytesAsync(name, System.Text.Encoding.UTF8.GetBytes(text));

    public async Task SaveAttachmentAsync(Attachment a)
    {
        var bytes = await _storage.ReadAttachmentAsync(a);
        if (bytes == null) { SetNotice(L["فایل دیگر در دسترس نیست."]); return; }
        await SaveBytesAsync(a.Name, bytes);
    }

    /// <summary>Saves the image that is open in the viewer (stored file, data: URI or http URL).</summary>
    public async Task SaveViewerAsync()
    {
        var v = Viewer;
        if (v == null) return;

        if (v.Att != null) { await SaveAttachmentAsync(v.Att); return; }

        try
        {
            byte[] bytes;
            string mime = "image/png";

            if (v.Src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = v.Src.IndexOf(',');
                var semi = v.Src.IndexOf(';');
                if (comma < 0) throw new InvalidOperationException("Invalid image data.");
                if (semi > 5 && semi < comma) mime = v.Src[5..semi];
                bytes = Convert.FromBase64String(v.Src[(comma + 1)..]);
            }
            else
            {
                using var resp = await _http.GetAsync(v.Src);
                resp.EnsureSuccessStatusCode();
                mime = resp.Content.Headers.ContentType?.MediaType ?? "image/png";
                bytes = await resp.Content.ReadAsByteArrayAsync();
            }

            var name = string.IsNullOrWhiteSpace(v.Name) || v.Name == "image" ? "image" : v.Name;
            if (string.IsNullOrEmpty(Path.GetExtension(name))) name += FileExportService.ExtensionFromMime(mime);
            await SaveBytesAsync(name, bytes);
        }
        catch (Exception ex)
        {
            _log.Error("Downloading image failed", ex);
            SetNotice(L["دانلود تصویر ناموفق بود: "] + ex.Message);
        }
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

    // File size limits (MB), configurable in Settings -> Chat.
    public int LimitImageMb { get; private set; } = 25;
    public int LimitTextMb { get; private set; } = 2;
    public int LimitPdfMb { get; private set; } = 50;
    public bool ShrinkImages { get; private set; } = true;

    public void SetLimits(int? imageMb = null, int? textMb = null, int? pdfMb = null, bool? shrink = null)
    {
        if (imageMb is int i) { LimitImageMb = Math.Clamp(i, 1, 500); Preferences.Default.Set("limit_image_mb", LimitImageMb); }
        if (textMb is int t) { LimitTextMb = Math.Clamp(t, 1, 100); Preferences.Default.Set("limit_text_mb", LimitTextMb); }
        if (pdfMb is int p) { LimitPdfMb = Math.Clamp(p, 1, 500); Preferences.Default.Set("limit_pdf_mb", LimitPdfMb); }
        if (shrink is bool s) { ShrinkImages = s; Preferences.Default.Set("shrink_images", s); }
        Notify();
    }

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
        if (Pending.Count >= MaxAttachments) return L.F("حداکثر {0} فایل در هر پیام مجاز است.", MaxAttachments);

        var (att, error) = await CreateAttachmentAsync(name, stream);
        if (att == null) return error;

        Pending.Add(att);
        if (att.Kind == "pdf") await AskPdfModeAsync(att);
        Notify();
        return null;
    }

    /// <summary>Copies an image from a reply into the next message (e.g. to upscale or vary a generated image).</summary>
    public async Task UseAsInputAsync(Attachment a)
    {
        if (Pending.Count >= MaxAttachments) { SetNotice(L.F("حداکثر {0} فایل در هر پیام مجاز است.", MaxAttachments)); return; }
        var bytes = await _storage.ReadAttachmentAsync(a);
        if (bytes == null) { SetNotice(L["فایل دیگر در دسترس نیست."]); return; }
        var copy = await _storage.SaveAttachmentAsync(a.Name, a.MimeType, a.Kind, bytes);
        Pending.Add(copy);
        ShowToast(L["به پیام بعدی اضافه شد ✓"]);
        Notify();
    }

    /// <summary>Validates a file and stores it. Used for message attachments and project files.</summary>
    public async Task<(Attachment? Att, string? Error)> CreateAttachmentAsync(string name, Stream stream)
    {
        name = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(name)) name = "file";

        var ext = Path.GetExtension(name).ToLowerInvariant();
        var isImage = ImageMimes.TryGetValue(ext, out var mime);
        var isPdf = ext == ".pdf";
        var limitMb = isImage ? LimitImageMb : isPdf ? LimitPdfMb : LimitTextMb;
        var limit = (long)limitMb * 1024 * 1024;

        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(buffer)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (ms.Length > limit)
                return (null, L.F("«{0}» بزرگ‌تر از حد مجاز است (حداکثر {1}).", name, L.F("{0} مگابایت", limitMb)));
        }
        var data = ms.ToArray();
        if (data.Length == 0) return (null, L.F("«{0}» خالی است.", name));

        // big photos: shrink to 2048 px JPEG so that services with a 5 MB image limit accept them
        if (isImage && ShrinkImages && ext != ".gif" && data.Length > 3 * 1024 * 1024)
        {
            var small = PhotoHelper.Downscale(data);
            if (small.Length < data.Length)
            {
                data = small;
                mime = "image/jpeg";
                name = Path.ChangeExtension(name, ".jpg");
            }
        }

        if (isPdf)
        {
            if (!PdfHelper.LooksLikePdf(data)) return (null, L.F("«{0}» یک فایل PDF معتبر نیست.", name));
            if (PdfHelper.ExtractText(data).Length == 0)
                return (null, L.F("«{0}» متنی ندارد (احتمالاً اسکن‌شده است).", name));
            var pdf = await _storage.SaveAttachmentAsync(name, "application/pdf", "pdf", data);
            return (pdf, null);
        }

        if (!isImage)
        {
            // anything with NUL bytes is binary (pdf, zip, docx, ...)
            var head = Math.Min(data.Length, 8192);
            for (var i = 0; i < head; i++)
                if (data[i] == 0)
                    return (null, L.F("«{0}»: فقط عکس (png, jpg, gif, webp)، PDF و فایل‌های متنی/کد پشتیبانی می‌شوند.", name));
            mime = "text/plain";
        }

        var att = await _storage.SaveAttachmentAsync(name, mime!, isImage ? "image" : "text", data);
        return (att, null);
    }

    public void DeleteAttachmentFiles(IEnumerable<Attachment> files) => _storage.DeleteAttachments(files.ToList());

    /// <summary>PDF waiting for the user's choice: extracted text or the original file.</summary>
    public Attachment? PdfPrompt { get; private set; }
    public bool PdfPromptTruncated { get; private set; }

    async Task AskPdfModeAsync(Attachment pdf)
    {
        var bytes = await _storage.ReadAttachmentAsync(pdf);
        PdfPromptTruncated = bytes != null && PdfHelper.ExtractText(bytes, pdf.Id).EndsWith("[... text truncated ...]");
        PdfPrompt = pdf;
    }

    public void ChoosePdfMode(string mode)
    {
        if (PdfPrompt != null) PdfPrompt.PdfMode = mode == "file" ? "file" : "text";
        PdfPrompt = null;
        Notify();
    }

    public void RemovePending(Attachment a)
    {
        if (Pending.Remove(a)) _storage.DeleteAttachments(new[] { a });
        Notify();
    }

    public Task<byte[]?> ReadAttachmentAsync(Attachment a) => _storage.ReadAttachmentAsync(a);

    // ------------------------------------------------------------ projects

    public const int MaxProjectFiles = 10;

    public List<Project> Projects { get; private set; } = new();

    /// <summary>Project of the chat that is about to be created (a new chat started inside a project).</summary>
    public string? DraftProjectId { get; private set; }

    public Project? ProjectOf(Conversation? c) =>
        c?.ProjectId == null ? null : Projects.FirstOrDefault(p => p.Id == c.ProjectId);

    public Project? CurrentProject =>
        Active != null ? ProjectOf(Active) : Projects.FirstOrDefault(p => p.Id == DraftProjectId);

    public bool ProjectDialogOpen { get; private set; }
    public Project? ProjectDialogTarget { get; private set; } // null = new project

    public void OpenProjectDialog(Project? p)
    {
        ProjectDialogTarget = p;
        ProjectDialogOpen = true;
        if (IsNarrow) SidebarOpen = false;
        Notify();
    }

    public void CloseProjectDialog()
    {
        ProjectDialogOpen = false;
        ProjectDialogTarget = null;
        Notify();
    }

    public async Task SaveProjectAsync(Project edited)
    {
        var copy = edited.Clone();
        var idx = Projects.FindIndex(p => p.Id == copy.Id);
        if (idx >= 0)
        {
            // files that were removed in the dialog are deleted from disk
            var kept = copy.Files.Select(f => f.Id).ToHashSet();
            _storage.DeleteAttachments(Projects[idx].Files.Where(f => !kept.Contains(f.Id)));
            Projects[idx] = copy;
        }
        else
        {
            Projects.Add(copy);
        }

        Notify();
        await _storage.SaveProjectsAsync(Projects);
    }

    /// <summary>Deletes the project and its files. Its conversations are kept and moved out of the project.</summary>
    public async Task DeleteProjectAsync(Project p)
    {
        foreach (var c in Conversations.Where(c => c.ProjectId == p.Id)) c.ProjectId = null;
        if (DraftProjectId == p.Id) DraftProjectId = null;

        _storage.DeleteAttachments(p.Files);
        Projects.RemoveAll(x => x.Id == p.Id);

        Notify();
        await _storage.SaveProjectsAsync(Projects);
        await _storage.SaveConversationsAsync(Conversations);
    }

    public async Task MoveConversationAsync(Conversation c, string? projectId)
    {
        c.ProjectId = projectId;
        Notify();
        await _storage.SaveConversationsAsync(Conversations);
    }

    public void NewChatInProject(string projectId)
    {
        NewChat();
        DraftProjectId = projectId;
        Notify();
    }

    public bool IsStreaming { get; private set; }
    public bool ShowSettings { get; private set; }
    public bool? SidebarOpen { get; private set; }
    public bool IsNarrow { get; private set; }
    public string? Notice { get; private set; }

    /// <summary>"system", "light" or "dark".</summary>
    // appearance
    public int UiFontSize { get; private set; } = 16;
    public int UiWidth { get; private set; } = 768;   // 0 = full width
    public int UiLineHeight { get; private set; } = 160; // percent
    public string UiFont { get; private set; } = "default";

    public string UiSignature => $"{UiFontSize}|{UiWidth}|{UiLineHeight}|{UiFont}";

    public void SetUi(int? fontSize = null, int? width = null, int? lineHeight = null, string? font = null)
    {
        if (fontSize != null) UiFontSize = Math.Clamp(fontSize.Value, 12, 26);
        if (width != null) UiWidth = width.Value <= 0 ? 0 : Math.Clamp(width.Value, 480, 1400);
        if (lineHeight != null) UiLineHeight = Math.Clamp(lineHeight.Value, 120, 220);
        if (font != null) UiFont = font;
        Preferences.Default.Set("ui_fs", UiFontSize);
        Preferences.Default.Set("ui_w", UiWidth);
        Preferences.Default.Set("ui_lh", UiLineHeight);
        Preferences.Default.Set("ui_font", UiFont);
        Notify();
    }

    public void ResetUi() => SetUi(16, 768, 160, "default");

    public string Theme { get; private set; } = "system";

    public void SetTheme(string theme)
    {
        Theme = theme is "light" or "dark" ? theme : "system";
        Preferences.Default.Set("theme", Theme);
        Notify();
    }

    public string Language => L.Language;

    /// <summary>Switches the UI language; the layout direction follows it (it can still be changed in settings).</summary>
    public void SetLanguage(string lang)
    {
        L.SetLanguage(lang);
        Preferences.Default.Set("lang", L.Language);
        SetRtl(L.Language == "fa");
    }

    public bool IsRtl { get; private set; } = true;
    public string SystemPrompt { get; private set; } = "";

    // ------------------------------------------------------------ init

    public async Task InitAsync()
    {
        if (_initialized) return;
        _initialized = true;

        Providers = await _storage.LoadProvidersAsync();
        Projects = await _storage.LoadProjectsAsync();
        Conversations = (await _storage.LoadConversationsAsync() ?? new())
            .OrderByDescending(c => c.Updated).ToList();

        L.SetLanguage(Preferences.Default.Get("lang", "fa"));
        Theme = Preferences.Default.Get("theme", "system");
        UiFontSize = Preferences.Default.Get("ui_fs", 16);
        UiWidth = Preferences.Default.Get("ui_w", 768);
        UiLineHeight = Preferences.Default.Get("ui_lh", 160);
        UiFont = Preferences.Default.Get("ui_font", "default");
        LoadTestSettings();
        ModelTests = new(await _storage.LoadModelTestsAsync() ?? new());
        IsRtl = Preferences.Default.Get("rtl", true);
        SystemPrompt = Preferences.Default.Get("system_prompt", "");
        LimitImageMb = Preferences.Default.Get("limit_image_mb", 25);
        LimitTextMb = Preferences.Default.Get("limit_text_mb", 2);
        LimitPdfMb = Preferences.Default.Get("limit_pdf_mb", 50);
        ShrinkImages = Preferences.Default.Get("shrink_images", true);
        UserPresets = await _storage.LoadUserPresetsAsync();
        LockAfterMin = Preferences.Default.Get("lock_after_min", 0);
        WebSearchOn = Preferences.Default.Get("web_search", false);
        SearchEngine = Preferences.Default.Get("search_engine", WebSearchService.Ddg);
        SearchBaseUrl = Preferences.Default.Get("search_url", "");
        SearchKey = await _storage.LoadSearchKeyAsync();
        Locked = HasPin;
        // first run: show the language page (people who already used the app are not asked)
        if (!Preferences.Default.ContainsKey("lang_chosen") &&
            (Preferences.Default.ContainsKey("lang") || Preferences.Default.ContainsKey("provider")))
            Preferences.Default.Set("lang_chosen", true);
        NeedsLanguagePick = !Preferences.Default.Get("lang_chosen", false);
        if (NeedsLanguagePick && !Preferences.Default.ContainsKey("lang"))
        {
            var deviceFa = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";
            L.SetLanguage(deviceFa ? "fa" : "en");
            IsRtl = deviceFa;
        }
        ShortcutsJson = Preferences.Default.Get("shortcuts_json", "");
        Shortcuts = ShortcutHelper.Parse(ShortcutsJson, out _);

        var pid = Preferences.Default.Get("provider", "");
        var provider = Providers.FirstOrDefault(p => p.Id == pid) ?? Providers.FirstOrDefault();
        CurrentProviderId = provider?.Id;

        LoadModelMap();
        var model = Preferences.Default.Get("model", "");
        if (provider != null && !_modelByProvider.ContainsKey(provider.Id) && !string.IsNullOrWhiteSpace(model))
            _modelByProvider[provider.Id] = model; // older versions only remembered one global model
        CurrentModel = PickModel(provider);

        Notify();
    }

    // ------------------------------------------------------------ web search

    WebSearchService? _search;
    WebSearchService Search => _search ??= new WebSearchService(_http);

    public bool WebSearchOn { get; private set; }
    public string SearchEngine { get; private set; } = WebSearchService.Ddg;
    public string SearchBaseUrl { get; private set; } = "";
    public string SearchKey { get; private set; } = "";

    public void SetWebSearch(bool on)
    {
        WebSearchOn = on;
        Preferences.Default.Set("web_search", on);
        Notify();
        ShowToast(on ? L["جستجوی وب روشن شد"] : L["جستجوی وب خاموش شد"]);
    }

    public void SetSearchEngine(string engine)
    {
        SearchEngine = engine;
        Preferences.Default.Set("search_engine", engine);
        Notify();
    }

    public void SetSearchBaseUrl(string url)
    {
        SearchBaseUrl = url;
        Preferences.Default.Set("search_url", url);
    }

    public async Task SetSearchKeyAsync(string key)
    {
        SearchKey = key;
        await _storage.SaveSearchKeyAsync(key);
    }

    /// <summary>Runs one test search and returns a message for the settings page.</summary>
    public async Task<string> TestSearchAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var r = await Search.SearchAsync(SearchEngine, "latest news", SearchKey, SearchBaseUrl, 5, cts.Token);
            return r.Count > 0 ? L.F("جستجو کار می‌کند ✓ ({0} نتیجه)", r.Count) : L["جستجو انجام شد ولی نتیجه‌ای برنگشت."];
        }
        catch (Exception ex) { return L["جستجوی وب ناموفق بود: "] + ex.Message; }
    }

    // ------------------------------------------------------------ app lock (PIN)

    public bool Locked { get; private set; }
    public bool HasPin => !string.IsNullOrEmpty(Preferences.Default.Get("pin_hash", ""));

    /// <summary>0 = lock only when the app starts; otherwise lock after this many minutes in the background.</summary>
    public int LockAfterMin { get; private set; }

    int _pinFails;
    DateTime _pinBlockedUntil;
    DateTime? _leftAt;

    /// <returns>An error text, or null when the PIN was set.</returns>
    public string? SetPin(string pin, string repeat)
    {
        if (pin.Length < PinHelper.MinLength) return L.F("رمز باید دست‌کم {0} کاراکتر باشد.", PinHelper.MinLength);
        if (pin != repeat) return L["دو رمز یکسان نیستند."];
        var salt = PinHelper.NewSalt();
        Preferences.Default.Set("pin_salt", salt);
        Preferences.Default.Set("pin_hash", PinHelper.Hash(pin, salt));
        Notify();
        return null;
    }

    public bool RemovePin(string current)
    {
        if (!PinHelper.Verify(current, Preferences.Default.Get("pin_salt", ""), Preferences.Default.Get("pin_hash", "")))
            return false;
        Preferences.Default.Remove("pin_salt");
        Preferences.Default.Remove("pin_hash");
        Locked = false;
        Notify();
        return true;
    }

    /// <returns>0 when unlocked or the PIN was wrong (no pause); otherwise the seconds to wait before the next try.</returns>
    public int TryUnlock(string pin)
    {
        var remaining = (int)Math.Ceiling((_pinBlockedUntil - DateTime.UtcNow).TotalSeconds);
        if (remaining > 0) return remaining;

        if (PinHelper.Verify(pin, Preferences.Default.Get("pin_salt", ""), Preferences.Default.Get("pin_hash", "")))
        {
            _pinFails = 0;
            Locked = false;
            Notify();
            return 0;
        }

        _pinFails++;
        if (_pinFails < 5) return 0;
        var wait = (int)Math.Min(300, 5 * Math.Pow(2, _pinFails - 5));
        _pinBlockedUntil = DateTime.UtcNow.AddSeconds(wait);
        return wait;
    }

    public void LockNow()
    {
        if (!HasPin) return;
        Locked = true;
        Notify();
    }

    public void SetLockAfter(string minutes)
    {
        LockAfterMin = int.TryParse(minutes, out var m) ? m : 0;
        Preferences.Default.Set("lock_after_min", LockAfterMin);
        Notify();
    }

    /// <summary>The app window lost focus / was hidden.</summary>
    public void OnLeft() => _leftAt ??= DateTime.UtcNow;

    /// <summary>The app window came back.</summary>
    public void OnReturned()
    {
        var left = _leftAt;
        _leftAt = null;
        if (left == null || !HasPin || LockAfterMin <= 0 || Locked) return;
        if (DateTime.UtcNow - left.Value >= TimeSpan.FromMinutes(LockAfterMin))
        {
            Locked = true;
            Notify();
        }
    }

    // ------------------------------------------------------------ backup

    public async Task CreateBackupAsync(bool includeKeys)
    {
        try
        {
            var data = await _storage.CreateBackupAsync(includeKeys);
            await SaveBytesAsync($"AiChat-backup-{DateTime.Now:yyyyMMdd-HHmm}.zip", data);
        }
        catch (Exception ex)
        {
            _log.Error("Creating the backup failed", ex);
            SetNotice(L["ذخیره ناموفق بود: "] + ex.Message);
        }
    }

    /// <summary>Restores a backup zip and reloads everything. Returns an error text, or null.</summary>
    public async Task<string?> RestoreBackupAsync(Stream zip)
    {
        Stop();
        var err = await _storage.RestoreBackupAsync(zip);
        if (err == "NOT_BACKUP") return L["این فایل یک نسخه پشتیبان AI Chat نیست."];
        if (err != null) return err;

        Active = null;
        Pending.Clear();
        Providers = await _storage.LoadProvidersAsync();
        Projects = await _storage.LoadProjectsAsync();
        Conversations = (await _storage.LoadConversationsAsync() ?? new()).OrderByDescending(c => c.Updated).ToList();
        UserPresets = await _storage.LoadUserPresetsAsync();
        ModelTests = new(await _storage.LoadModelTestsAsync() ?? new());
        var provider = Providers.FirstOrDefault(p => p.Id == CurrentProviderId) ?? Providers.FirstOrDefault();
        CurrentProviderId = provider?.Id;
        CurrentModel = PickModel(provider);
        Notify();
        return null;
    }

    // ------------------------------------------------------------ remembered model per provider

    Dictionary<string, string> _modelByProvider = new();

    void LoadModelMap()
    {
        try
        {
            var json = Preferences.Default.Get("models_by_provider", "");
            _modelByProvider = string.IsNullOrWhiteSpace(json)
                ? new()
                : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
        catch
        {
            _modelByProvider = new();
        }
    }

    void SaveModelMap() =>
        Preferences.Default.Set("models_by_provider", System.Text.Json.JsonSerializer.Serialize(_modelByProvider));

    /// <summary>The model last chosen for this provider; falls back to its default if it no longer exists.</summary>
    string PickModel(ProviderConfig? p)
    {
        if (p == null) return "";
        if (_modelByProvider.TryGetValue(p.Id, out var m) && !string.IsNullOrWhiteSpace(m) &&
            (m == p.DefaultModel || p.Models.Contains(m)))
            return m;
        return FirstModel(p);
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

    /// <summary>"general", "api", "chat", "data", "about" or null (on phones: the section list).</summary>
    public string? SettingsSection { get; private set; }

    public void OpenSettings()
    {
        SettingsSection = null;
        ShowSettings = true;
        if (IsNarrow) SidebarOpen = false;
        Notify();
    }

    public void OpenSettingsAt(string section)
    {
        OpenSettings();
        SettingsSection = section;
        Notify();
    }

    public void SetSettingsSection(string? section) { SettingsSection = section; Notify(); }
    public void CloseSettings() { ShowSettings = false; SettingsSection = null; Notify(); }

    // ------------------------------------------------------------ back navigation

    /// <summary>The project dialog registers its own "cancel" here so it can clean up files it uploaded.</summary>
    public Action? ProjectDialogCancelHandler { get; set; }

    /// <summary>
    /// Closes the top-most layer (viewer, dialogs, settings page, sidebar).
    /// Returns false when nothing was open, so the caller can let the app go to the background.
    /// </summary>
    /// <summary>"provider" / "model" while the centered picker sheet is open.</summary>
    public string? Picker { get; private set; }
    public string PickerFilter { get; private set; } = "";
    public bool NewApiRequested { get; set; }
    public void OpenNewApi() { Picker = null; NewApiRequested = true; OpenSettingsAt("api"); }

    // ---- model test: which models of the current API actually answer
    // ================= model test =================
    // Results are saved (model_tests.json) and survive restarts until a new test replaces them.

    public System.Collections.Concurrent.ConcurrentDictionary<string, ModelTestResult> ModelTests { get; private set; } = new();
    public bool TestingModels { get; private set; }
    public int TestDone { get; private set; }
    public int TestTotal { get; private set; }

    /// <summary>"all" | "ok" | "failed" | "limited" | "untested": which previous results the model list shows.</summary>
    public string TestFilter { get; set; } = "all";

    /// <summary>Shown while the test waits for a rate limit to reset, or after it gave up.</summary>
    public string? TestMessage { get; private set; }

    // ---- user settings of the test (saved)
    int _testBatch, _testWindow = 60, _testParallel = 3;
    bool _testContinue = true;
    string _testWords = "";

    /// <summary>Max models tested per period; 0 = automatic (learned from the first rate-limit answer).</summary>
    public int TestBatch { get => _testBatch; set { _testBatch = Math.Clamp(value, 0, 1000); Preferences.Default.Set("test_batch", _testBatch); } }
    /// <summary>Length of that period in seconds.</summary>
    public int TestWindow { get => _testWindow; set { _testWindow = Math.Clamp(value, 5, 3600); Preferences.Default.Set("test_window", _testWindow); } }
    public int TestParallel { get => _testParallel; set { _testParallel = Math.Clamp(value, 1, 6); Preferences.Default.Set("test_parallel", _testParallel); } }
    /// <summary>true = wait for the limit to reset and continue; false = stop at the first limit.</summary>
    public bool TestContinueOnLimit { get => _testContinue; set { _testContinue = value; Preferences.Default.Set("test_continue", value); } }
    /// <summary>Extra words (comma separated); an error containing one of them counts as a rate limit.</summary>
    public string TestLimitWords { get => _testWords; set { _testWords = value ?? ""; Preferences.Default.Set("test_words", _testWords); } }
    /// <summary>Text of the last error that was recognized as a rate limit.</summary>
    public string LastLimitMessage { get; private set; } = "";

    public void UseLastLimitMessageAsKeyword()
    {
        if (string.IsNullOrWhiteSpace(LastLimitMessage)) return;
        var word = LastLimitMessage.Length > 80 ? LastLimitMessage[..80] : LastLimitMessage;
        TestLimitWords = string.IsNullOrWhiteSpace(TestLimitWords) ? word : TestLimitWords.TrimEnd(' ', ',') + ", " + word;
        Notify();
    }

    void LoadTestSettings()
    {
        _testBatch = Preferences.Default.Get("test_batch", 0);
        _testWindow = Preferences.Default.Get("test_window", 60);
        _testParallel = Preferences.Default.Get("test_parallel", 3);
        _testContinue = Preferences.Default.Get("test_continue", true);
        _testWords = Preferences.Default.Get("test_words", "");
        LastLimitMessage = Preferences.Default.Get("last_limit_msg", "");
    }

    CancellationTokenSource? _testCts;
    readonly object _rateLock = new();
    readonly List<DateTime> _starts = new();   // reserved request start times (sliding window)
    readonly List<DateTime> _okTimes = new();  // recent successful answers, used to learn the limit
    DateTime _pauseUntil, _pauseSetAt;
    int _learnedBatch;                         // limit learned from the server (0 = none yet)
    string? _abortReason;
    readonly SemaphoreSlim _testSaveGate = new(1, 1);

    public static string TestKey(string providerId, string model) => providerId + "|" + model;

    public ModelTestResult? TestResultOf(string model) =>
        CurrentProviderId != null && ModelTests.TryGetValue(TestKey(CurrentProviderId, model), out var r) ? r : null;

    public void StopModelTest() => _testCts?.Cancel();

    public async Task ClearModelTestsAsync()
    {
        if (CurrentProviderId == null || TestingModels) return;
        foreach (var k in ModelTests.Keys.Where(k => k.StartsWith(CurrentProviderId + "|")).ToList())
            ModelTests.TryRemove(k, out _);
        Notify();
        await SaveModelTestsAsync();
    }

    async Task SaveModelTestsAsync()
    {
        await _testSaveGate.WaitAsync();
        try { await _storage.SaveModelTestsAsync(new Dictionary<string, ModelTestResult>(ModelTests)); }
        catch (Exception ex) { _log.Warn("Saving model test results failed: " + ex.Message); }
        finally { _testSaveGate.Release(); }
    }

    int EffectiveBatch()
    {
        if (_testBatch > 0 && _learnedBatch > 0) return Math.Min(_testBatch, _learnedBatch);
        return Math.Max(_testBatch, _learnedBatch); // 0 = unlimited
    }

    /// <summary>Reserves the next allowed start time: after the global pause and within "batch per window".</summary>
    DateTime ReserveStart()
    {
        lock (_rateLock)
        {
            var window = TimeSpan.FromSeconds(_testWindow);
            var start = DateTime.UtcNow;
            if (_pauseUntil > start) start = _pauseUntil;
            var batch = EffectiveBatch();
            if (batch > 0)
            {
                while (true)
                {
                    var recent = _starts.Where(t => t > start - window).OrderBy(t => t).ToList();
                    if (recent.Count < batch) break;
                    start = recent[recent.Count - batch] + window;
                }
            }
            _starts.Add(start);
            _starts.RemoveAll(t => t < DateTime.UtcNow - window - window);
            return start;
        }
    }

    async Task WaitUntilAsync(DateTime start, CancellationToken ct)
    {
        while (_abortReason == null)
        {
            var now = DateTime.UtcNow;
            var target = start > _pauseUntil ? start : _pauseUntil;   // a pause may have started after we reserved
            var wait = target - now;
            if (wait <= TimeSpan.Zero) { if (_pauseUntil <= now) SetPacingMessage(); return; }

            if (_pauseUntil > now)
                TestMessage = L.F("محدودیت درخواست سرویس؛ ادامه‌ی تست تا {0} ثانیه‌ی دیگر...", (int)Math.Ceiling((_pauseUntil - now).TotalSeconds));
            else SetPacingMessage();
            Notify();
            await Task.Delay(wait < TimeSpan.FromSeconds(1) ? wait : TimeSpan.FromSeconds(1), ct);
        }
    }

    void SetPacingMessage()
    {
        if (_abortReason != null) return;
        var batch = EffectiveBatch();
        TestMessage = batch > 0 && (_learnedBatch > 0 || _testBatch > 0)
            ? L.F("تست ادامه دارد؛ حداکثر {0} مدل در هر {1} ثانیه.", batch, _testWindow)
            : null;
    }

    static List<string> SplitWords(string s) =>
        s.Split(new[] { ',', '،', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public async Task TestModelsAsync(List<string> models)
    {
        var provider = CurrentProvider;
        if (provider == null || models.Count == 0 || TestingModels) return;

        TestingModels = true;
        TestDone = 0;
        TestTotal = models.Count;
        TestMessage = null;
        _abortReason = null;
        _learnedBatch = 0;
        _pauseUntil = _pauseSetAt = DateTime.MinValue;
        lock (_rateLock) { _starts.Clear(); _okTimes.Clear(); }
        _testCts = new CancellationTokenSource();
        var all = _testCts.Token;
        var impl = _factory.Create(provider.Type);
        var words = SplitWords(_testWords);
        var next = -1;
        _log.Info($"Model test: {provider.Name}, {models.Count} models, batch={_testBatch}/{_testWindow}s, parallel={_testParallel}, continue={_testContinue}");
        Notify();

        async Task TestOne(string model)
        {
            ModelTestResult? result = null;
            var limitHits = 0;
            for (var loop = 0; loop < 12 && result == null; loop++)
            {
                if (_abortReason != null || all.IsCancellationRequested) return;
                var start = ReserveStart();
                try { await WaitUntilAsync(start, all); }
                catch (OperationCanceledException) { return; }
                if (_abortReason != null) return;

                var t0 = DateTime.UtcNow;
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(all);
                    cts.CancelAfter(TimeSpan.FromSeconds(30));
                    var hi = new List<ChatMessage> { new() { Role = "user", Content = "Hi" } };
                    await foreach (var _ in impl.StreamAsync(provider, model, hi, "Reply with one word.", null, cts.Token))
                        break;
                    result = new ModelTestResult(true, null, false, DateTime.Now);
                    lock (_rateLock) _okTimes.Add(DateTime.UtcNow);
                }
                catch (OperationCanceledException) when (!all.IsCancellationRequested)
                {
                    result = new ModelTestResult(false, "Timeout (30 s)", false, DateTime.Now);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    var d = ProviderHttp.RateInfo(ex, words);
                    if (!d.Limited)
                    {
                        result = new ModelTestResult(false, ProviderHttp.Describe(ex), false, DateTime.Now);
                        break;
                    }

                    LastLimitMessage = d.Text.Length > 300 ? d.Text[..300] : d.Text;
                    Preferences.Default.Set("last_limit_msg", LastLimitMessage);

                    if (d.Daily || d.RetrySeconds > 900)
                    {
                        _abortReason = L["سهمیه‌ی روزانه یا طولانی‌مدت این API تمام شده است؛ تست متوقف شد."];
                        result = new ModelTestResult(false, LastLimitMessage, true, DateTime.Now);
                        break;
                    }

                    // An answer to a request that started before the current pause: it is the same limit, not a new one.
                    bool stale;
                    lock (_rateLock) stale = t0 < _pauseSetAt;
                    if (stale) continue;

                    if (!_testContinue)
                    {
                        _abortReason = L["به محدودیت رسید و طبق تنظیمات، تست متوقف شد."];
                        result = new ModelTestResult(false, LastLimitMessage, true, DateTime.Now);
                        break;
                    }

                    limitHits++;
                    lock (_rateLock)
                    {
                        var now = DateTime.UtcNow;
                        var wait = TimeSpan.FromSeconds(Math.Min(d.RetrySeconds ?? _testWindow, 900) + 1);
                        _pauseSetAt = now;
                        _pauseUntil = now + wait;
                        _starts.Clear();

                        // learn the allowed speed; limited again after learning -> be more careful
                        var win = now - TimeSpan.FromSeconds(_testWindow);
                        _okTimes.RemoveAll(t => t < win);
                        _learnedBatch = _learnedBatch == 0 ? Math.Max(1, _okTimes.Count) : Math.Max(1, _learnedBatch * 2 / 3);
                        _okTimes.Clear();
                    }
                    _log.Warn($"Model test: rate limit ({provider.Name}), pausing; allowed about {_learnedBatch} per {_testWindow}s");
                    if (limitHits >= 4) result = new ModelTestResult(false, LastLimitMessage, true, DateTime.Now);
                }
            }
            result ??= _abortReason == null && !all.IsCancellationRequested
                ? new ModelTestResult(false, LastLimitMessage, true, DateTime.Now) : null;

            if (result != null)
            {
                ModelTests[TestKey(provider.Id, model)] = result;
                var done = Interlocked.Increment(ref _testDoneCounter);
                TestDone = done;
                Notify();
                if (done % 15 == 0) await SaveModelTestsAsync();
            }
        }

        async Task Worker()
        {
            while (!all.IsCancellationRequested && _abortReason == null)
            {
                var i = Interlocked.Increment(ref next);
                if (i >= models.Count) return;
                await TestOne(models[i]);
            }
        }

        _testDoneCounter = 0;
        try { await Task.WhenAll(Enumerable.Range(0, _testParallel).Select(_ => Worker())); }
        finally
        {
            TestingModels = false;
            TestMessage = _abortReason;
            _testCts?.Dispose();
            _testCts = null;
            Notify();
            await SaveModelTestsAsync();
        }
    }

    int _testDoneCounter;

    public void OpenPicker(string kind) { Picker = kind; PickerFilter = ""; Notify(); }
    public void ClosePicker() { Picker = null; Notify(); }
    public void SetPickerFilter(string f) { PickerFilter = f; Notify(); }

    // small modals (custom select lists etc.) register here so Esc / Android back closes the top one first
    readonly List<Action> _backStack = new();
    public void PushBack(Action close) { if (!_backStack.Contains(close)) _backStack.Add(close); }
    public void RemoveBack(Action close) => _backStack.Remove(close);

    /// <summary>Provider whose edit form Settings should open directly (from the provider picker).</summary>
    public string? EditProviderId { get; set; }
    public void OpenEditApi(string providerId) { Picker = null; EditProviderId = providerId; OpenSettingsAt("api"); }

    public bool HandleBack(bool includeSidebar = true)
    {
        if (_backStack.Count > 0)
        {
            var top = _backStack[^1];
            _backStack.RemoveAt(_backStack.Count - 1);
            top();
            return true;
        }
        if (PdfPrompt != null) { ChoosePdfMode("text"); return true; }
        if (Picker != null) { ClosePicker(); return true; }
        if (Viewer != null) { CloseViewer(); return true; }
        if (ShowLogs) { CloseLogs(); return true; }

        if (ProjectDialogOpen)
        {
            if (ProjectDialogCancelHandler != null) ProjectDialogCancelHandler();
            else CloseProjectDialog();
            return true;
        }

        if (ShowSettings)
        {
            if (IsNarrow && SettingsSection != null) SetSettingsSection(null); // section -> section list
            else CloseSettings();                                               // list -> chat
            return true;
        }

        if (SearchOpen) { CloseSearch(); return true; }

        if (includeSidebar && IsNarrow && SidebarOpen == true) { CloseSidebar(); return true; }
        return false;
    }

    public void SetNotice(string? text) { Notice = text; Notify(); }
    public void ClearNotice() => SetNotice(null);

    public void SetRtl(bool rtl)
    {
        IsRtl = rtl;
        Preferences.Default.Set("rtl", rtl);
        Notify();
    }

    // ------------------------------------------------------------ first-run language

    public bool NeedsLanguagePick { get; private set; }

    public void ChooseLanguage(string lang)
    {
        SetLanguage(lang);
        Preferences.Default.Set("lang_chosen", true);
        NeedsLanguagePick = false;
        Notify();
    }

    // ------------------------------------------------------------ user presets (saved API templates)

    public List<ProviderConfig> UserPresets { get; private set; } = new();

    /// <summary>Saves the API settings (without the key) as a reusable template under the given name.</summary>
    public async Task SaveUserPresetAsync(ProviderConfig cfg, string name)
    {
        var copy = cfg.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.ApiKey = "";
        copy.Name = name.Trim();
        UserPresets.RemoveAll(p => p.Name.Equals(copy.Name, StringComparison.OrdinalIgnoreCase));
        UserPresets.Add(copy);
        UserPresets = UserPresets.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        await _storage.SaveUserPresetsAsync(UserPresets);
        Notify();
    }

    public async Task DeleteUserPresetAsync(string name)
    {
        UserPresets.RemoveAll(p => p.Name == name);
        await _storage.SaveUserPresetsAsync(UserPresets);
        Notify();
    }

    // ------------------------------------------------------------ slash shortcuts

    public List<Shortcut> Shortcuts { get; private set; } = new();
    public string ShortcutsJson { get; private set; } = "";

    /// <summary>Saves the shortcuts JSON. Returns an error text, or null on success.</summary>
    public string? SetShortcutsJson(string json)
    {
        var list = ShortcutHelper.Parse(json, out var error);
        if (error != null) return error;
        Shortcuts = list;
        ShortcutsJson = json ?? "";
        Preferences.Default.Set("shortcuts_json", ShortcutsJson);
        Notify();
        return null;
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
        CurrentModel = PickModel(p);
        PersistSelection();
        Notify();
    }

    public void SelectModel(string? model)
    {
        CurrentModel = model ?? "";
        if (CurrentProviderId != null && !string.IsNullOrWhiteSpace(CurrentModel))
        {
            _modelByProvider[CurrentProviderId] = CurrentModel;
            SaveModelMap();
        }
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
        if (_modelByProvider.Remove(id)) SaveModelMap();

        if (CurrentProviderId == id)
        {
            var next = Providers.FirstOrDefault();
            CurrentProviderId = next?.Id;
            CurrentModel = PickModel(next);
            PersistSelection();
        }

        Notify();
        await _storage.SaveProvidersAsync(Providers);
    }

    public async Task<List<string>> FetchModelsAsync(ProviderConfig cfg, CancellationToken ct)
    {
        try
        {
            var list = await _factory.Create(cfg.Type).ListModelsAsync(cfg, ct);
            _log.Info($"Fetched {list.Count} models from {cfg.Name}");
            return list;
        }
        catch (Exception ex)
        {
            _log.Error($"Fetching models from {cfg.Name} failed", ex);
            throw;
        }
    }

    // ------------------------------------------------------------ conversations

    public void NewChat()
    {
        Stop();
        SearchOpen = false;
        Active = null;
        DraftProjectId = null;
        if (IsNarrow) SidebarOpen = false;
        Notify();
    }

    public void OpenConversation(Conversation c)
    {
        Stop();
        SearchOpen = false;
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

    /// <summary>Every attachment in these messages, including edited versions and their tails.</summary>
    static IEnumerable<Attachment> AllAttachments(IEnumerable<ChatMessage> messages)
    {
        foreach (var m in messages)
        {
            foreach (var a in m.Attachments) yield return a;
            if (m.Versions == null) continue;
            foreach (var v in m.Versions)
            {
                foreach (var a in v.Attachments) yield return a;
                foreach (var a in AllAttachments(v.Tail)) yield return a;
            }
        }
    }

    // ------------------------------------------------------------ editing messages (versions)

    void SnapshotCurrent(ChatMessage msg, List<ChatMessage> tail)
    {
        msg.Versions ??= new List<MessageVersion>();
        if (msg.Versions.Count == 0) msg.Versions.Add(new MessageVersion());
        if (msg.VersionIndex >= msg.Versions.Count) msg.VersionIndex = msg.Versions.Count - 1;

        var slot = msg.Versions[msg.VersionIndex];
        slot.Content = msg.Content;
        slot.Attachments = msg.Attachments.ToList();
        slot.Tail = tail;
    }

    static void ClearActiveSlot(ChatMessage msg)
    {
        msg.Versions![msg.VersionIndex] = new MessageVersion();
    }

    /// <summary>Edits a user message: the old text and everything after it is kept as an earlier version.</summary>
    public async Task EditAndResendAsync(ChatMessage msg, string newText)
    {
        newText = (newText ?? string.Empty).Trim();
        if (IsStreaming || Active == null || msg.Role != "user") return;
        if (newText.Length == 0 && msg.Attachments.Count == 0) return;

        var err = ValidateSend();
        if (err != null) { SetNotice(err); return; }

        var conv = Active;
        var idx = conv.Messages.IndexOf(msg);
        if (idx < 0) return;

        var tail = conv.Messages.Skip(idx + 1).ToList();
        SnapshotCurrent(msg, tail);
        conv.Messages.RemoveRange(idx + 1, conv.Messages.Count - idx - 1);

        msg.Versions!.Add(new MessageVersion());
        msg.VersionIndex = msg.Versions.Count - 1;
        msg.Content = newText;

        var provider = CurrentProvider!;
        conv.ProviderId = provider.Id;
        conv.Model = CurrentModel;
        Notice = null;

        var reply = new ChatMessage { Role = "assistant", Model = $"{provider.Name} · {CurrentModel}" };
        conv.Messages.Add(reply);
        await RunAsync(conv, reply, provider, CurrentModel);
    }

    /// <summary>Shows the previous (delta = -1) or next (delta = +1) version of an edited message.</summary>
    public async Task SwitchVersionAsync(ChatMessage msg, int delta)
    {
        if (IsStreaming || Active == null || msg.Versions == null) return;

        var conv = Active;
        var idx = conv.Messages.IndexOf(msg);
        if (idx < 0) return;

        var target = Math.Clamp(msg.VersionIndex + delta, 0, msg.Versions.Count - 1);
        if (target == msg.VersionIndex) return;

        SnapshotCurrent(msg, conv.Messages.Skip(idx + 1).ToList());
        conv.Messages.RemoveRange(idx + 1, conv.Messages.Count - idx - 1);

        var v = msg.Versions[target];
        msg.VersionIndex = target;
        msg.Content = v.Content;
        msg.Attachments = v.Attachments.ToList();
        conv.Messages.AddRange(v.Tail);
        ClearActiveSlot(msg);

        Notify();
        await _storage.SaveConversationsAsync(Conversations);
    }

    public async Task TogglePinAsync(Conversation c)
    {
        c.Pinned = !c.Pinned;
        Notify();
        await _storage.SaveConversationsAsync(Conversations);
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
        _storage.DeleteAttachments(AllAttachments(c.Messages).ToList());
        Notify();
        await _storage.SaveConversationsAsync(Conversations);
    }

    public async Task ClearAllConversationsAsync()
    {
        Stop();
        Active = null;
        _storage.DeleteAttachments(AllAttachments(Conversations.SelectMany(c => c.Messages)).ToList());
        _storage.DeleteAttachments(Pending);
        Pending.Clear();
        Conversations.Clear();
        Notify();
        await _storage.SaveConversationsAsync(Conversations);
    }

    // ------------------------------------------------------------ sending

    public string? ValidateSend()
    {
        if (CurrentProvider == null) return L["ابتدا از بخش تنظیمات یک API اضافه کنید."];
        if (string.IsNullOrWhiteSpace(CurrentModel)) return L["یک مدل انتخاب کنید (یا نام مدل را در تنظیمات وارد کنید)."];
        return null;
    }

    public async Task SendAsync(string text)
    {
        text = ShortcutHelper.Expand(text.Trim(), Shortcuts);
        if (IsStreaming || (text.Length == 0 && Pending.Count == 0)) return;

        var err = ValidateSend();
        if (err != null) { SetNotice(err); return; }

        var provider = CurrentProvider!;
        Notice = null;

        var attachments = Pending.ToList();
        Pending.Clear();

        if (Active == null)
        {
            Active = new Conversation
            {
                Title = MakeTitle(text.Length > 0 ? text : attachments[0].Name),
                ProjectId = DraftProjectId
            };
            DraftProjectId = null;
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

    /// <summary>Progress line of a running async job (e.g. "running · 42% · 0:35"), shown in the waiting bubble.</summary>
    public string? GenStatus { get; private set; }

    public void Stop() => _cts?.Cancel();

    static bool LooksLikeImageRejection(string error)
    {
        var e = error.ToLowerInvariant();
        return e.Contains("image") || e.Contains("vision") || e.Contains("multimodal") || e.Contains("multi-modal") ||
               e.Contains("modalit") || e.Contains("visual");
    }

    /// <summary>Stops sending the images of this chat (and its project) after the model refused them.</summary>
    async Task<bool> ExcludeImagesAsync(List<ChatMessage> history, Project? project)
    {
        var any = false;
        foreach (var a in history.SelectMany(m => m.Attachments).Where(a => a.Kind == "image" && !a.Excluded))
        { a.Excluded = true; any = true; }

        // versions of edited messages keep their own snapshots; leave those alone (they are not sent)
        if (project != null)
        {
            var changed = false;
            foreach (var a in project.Files.Where(a => a.Kind == "image" && !a.Excluded)) { a.Excluded = true; changed = true; }
            if (changed) { any = true; await _storage.SaveProjectsAsync(Projects); }
        }
        if (any) _log.Warn("Model rejected images; image attachments are excluded from now on.");
        return any;
    }

    async Task RunAsync(Conversation conv, ChatMessage reply, ProviderConfig provider, string model)
    {
        IsStreaming = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Notify();

        var history = conv.Messages.TakeWhile(m => !ReferenceEquals(m, reply)).ToList();
        var impl = _factory.Create(provider.Type);

        // project: extra instructions go to the system prompt, reference files go in front of the first message
        var project = ProjectOf(conv);
        var fileCount = history.Sum(m => m.Attachments.Count);
        _log.Info($"Request: {provider.Name} ({provider.Type}) / {model}, {history.Count} messages, {fileCount} attachments" +
                  (project != null ? $", project '{project.Name}'" : ""));
        var system = string.Join("\n\n",
            new[] { SystemPrompt, project?.Instructions }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (project is { Files.Count: > 0 })
            history.Insert(0, new ChatMessage
            {
                Role = "user",
                Content = "(Reference files of this project)",
                Attachments = project.Files
            });

        List<SearchResult>? found = null;
        var stopped = false;
        try
        {
            if (WebSearchOn && !provider.IsGenerator &&
                history.LastOrDefault(m => m.Role == "user")?.Content?.Trim() is { Length: > 0 } query)
            {
                GenStatus = L["🔎 در حال جستجو در وب..."];
                Notify();
                try
                {
                    using var sc = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    sc.CancelAfter(TimeSpan.FromSeconds(20));
                    found = await Search.SearchAsync(SearchEngine, query, SearchKey, SearchBaseUrl, 5, sc.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    SetNotice(L["جستجوی وب ناموفق بود: "] + "timeout");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Warn("Web search failed: " + ex.Message);
                    SetNotice(L["جستجوی وب ناموفق بود: "] + ex.Message);
                }
                GenStatus = null;
                if (found is { Count: > 0 })
                    system = string.Join("\n\n", new[] { system, WebSearchService.BuildContext(found, DateTime.Now) }.Where(x => !string.IsNullOrWhiteSpace(x)));
            }

            // images generated by the model are stored as attachments of the reply
            async Task OnImage(string mime, byte[] data)
            {
                var n = reply.Attachments.Count + 1;
                var kind = mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "image"
                    : mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? "video"
                    : mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ? "audio" : "file";
                var att = await _storage.SaveAttachmentAsync(
                    $"{kind}-{n}{FileExportService.ExtensionFromMime(mime)}", mime, kind, data);
                reply.Attachments.Add(att);
                Notify();
            }

            await foreach (var chunk in impl.StreamAsync(provider, model, history, system, OnImage, ct))
            {
                if (chunk.StartsWith(ProviderStatus.Prefix, StringComparison.Ordinal))
                {
                    GenStatus = chunk[ProviderStatus.Prefix.Length..];
                    Notify();
                    continue;
                }
                reply.Content += chunk;

                // throttle UI updates to ~20/s
                if ((DateTime.UtcNow - _lastNotify).TotalMilliseconds > 50)
                {
                    _lastNotify = DateTime.UtcNow;
                    Notify();
                }
            }

            if (found is { Count: > 0 } && !string.IsNullOrEmpty(reply.Content))
                reply.Content += WebSearchService.SourcesMarkdown(found, L["منابع"]);
        }
        catch (OperationCanceledException)
        {
            stopped = true; // user pressed stop
        }
        catch (Exception ex)
        {
            reply.ErrorText = ProviderHttp.Describe(ex);
            if (!provider.IsGenerator && LooksLikeImageRejection(reply.ErrorText) && await ExcludeImagesAsync(history, project))
                reply.ErrorText += "\n\n" + L["این مدل عکس را پشتیبانی نمی‌کند. عکس‌ها از ادامه‌ی گفتگو کنار گذاشته شدند؛ می‌توانید پیام متنی بفرستید یا مدل دیگری را انتخاب کنید."];
            _log.Error($"Request to {provider.Name} ({provider.Type}) / {model} failed", ex);
        }
        finally
        {
            IsStreaming = false;
            GenStatus = null;
            _cts?.Dispose();
            _cts = null;

            if (string.IsNullOrEmpty(reply.Content) && reply.ErrorText == null && reply.Attachments.Count == 0)
            {
                if (stopped) conv.Messages.Remove(reply); // stopped before any text arrived -> drop the empty bubble
                else
                {
                    // the server answered but sent no text: say so instead of hiding the bubble
                    reply.ErrorText = L["مدل پاسخ خالی برگرداند. اگر مدل «تفکری» است، حداکثر طول پاسخ یا طول context مدل را بیشتر کنید؛ جزئیات در لاگ‌ها است."];
                    _log.Warn($"Empty answer from {provider.Name} / {model}");
                }
            }

            conv.Updated = DateTime.UtcNow;
            if (Conversations.Remove(conv)) Conversations.Insert(0, conv);

            Notify();
            await _storage.SaveConversationsAsync(Conversations);
        }
    }

    string MakeTitle(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? DefaultTitle;
        return line.Length > 40 ? line[..40] + "…" : line;
    }
}
