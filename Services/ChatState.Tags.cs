using Microsoft.Maui.Storage;
using AiChat.Models;

namespace AiChat.Services;

/// <summary>Chat labels (tags) with a filter in the chat list, and chat templates.</summary>
public partial class ChatState
{
    // ------------------------------------------------------------ tags

    public Conversation? TagEditTarget { get; private set; }
    readonly HashSet<string> _tagFilter = new(StringComparer.CurrentCultureIgnoreCase);

    public List<(string Tag, int Count)> AllTags => ChatTags.All(Conversations);

    /// <summary>The selected filter tags that still exist on some chat.</summary>
    public List<string> ActiveTagFilter
    {
        get
        {
            if (_tagFilter.Count == 0) return new();
            var all = AllTags.Select(t => t.Tag).ToList();
            return all.Where(t => _tagFilter.Contains(t)).ToList();
        }
    }

    public bool PassTagFilter(Conversation c)
    {
        var f = ActiveTagFilter;
        return f.Count == 0 || ChatTags.Matches(c, f);
    }

    public void ToggleTagFilter(string tag)
    {
        if (!_tagFilter.Remove(tag)) _tagFilter.Add(tag);
        Notify();
    }

    public void ClearTagFilter()
    {
        _tagFilter.Clear();
        Notify();
    }

    public void OpenTagEdit(Conversation c)
    {
        TagEditTarget = c;
        if (IsNarrow) SidebarOpen = false;
        Notify();
    }

    public void CloseTagEdit()
    {
        TagEditTarget = null;
        Notify();
    }

    public async Task AddTagAsync(Conversation c, string tag)
    {
        if (!ChatTags.Add(c.Tags, tag)) return;
        Notify();
        await _storage.SaveConversationsAsync(Conversations);
    }

    public async Task RemoveTagAsync(Conversation c, string tag)
    {
        if (!ChatTags.Remove(c.Tags, tag)) return;
        Notify();
        await _storage.SaveConversationsAsync(Conversations);
    }

    public async Task RenameTagAsync(string from, string to)
    {
        var wasFiltered = _tagFilter.Remove(from);
        if (ChatTags.RenameEverywhere(Conversations, from, to) > 0)
        {
            if (wasFiltered) _tagFilter.Add(ChatTags.Normalize(to));
            await _storage.SaveConversationsAsync(Conversations);
        }
        Notify();
    }

    public async Task DeleteTagAsync(string tag)
    {
        _tagFilter.Remove(tag);
        if (ChatTags.RemoveEverywhere(Conversations, tag) > 0)
            await _storage.SaveConversationsAsync(Conversations);
        Notify();
    }

    // ------------------------------------------------------------ templates

    public List<ChatTemplate> Templates { get; private set; } = new();
    public bool TemplatePickerOpen { get; private set; }
    ChatTemplate? _draftTemplate;

    void LoadTemplates() => Templates = ChatTags.FromJson(Preferences.Default.Get("chat_templates", ""));

    void SaveTemplates() => Preferences.Default.Set("chat_templates", ChatTags.ToJson(Templates));

    public void SaveTemplate(ChatTemplate t)
    {
        t.Name = t.Name.Trim();
        if (t.Name.Length == 0) return;
        if (string.IsNullOrWhiteSpace(t.Icon)) t.Icon = "📋";
        var i = Templates.FindIndex(x => x.Id == t.Id);
        if (i >= 0) Templates[i] = t; else Templates.Add(t);
        SaveTemplates();
        Notify();
    }

    public void DeleteTemplate(string id)
    {
        Templates.RemoveAll(t => t.Id == id);
        SaveTemplates();
        Notify();
    }

    /// <summary>Saves the settings of an existing chat as a template.</summary>
    public void SaveChatAsTemplate(Conversation c)
    {
        var t = ChatTags.FromConversation(c, c.Title);
        Templates.Add(t);
        SaveTemplates();
        ShowToast(L["قالب ذخیره شد"]);
        Notify();
    }

    public void OpenTemplatePicker()
    {
        TemplatePickerOpen = true;
        if (IsNarrow) SidebarOpen = false;
        Notify();
    }

    public void CloseTemplatePicker()
    {
        TemplatePickerOpen = false;
        Notify();
    }

    /// <summary>Starts a new empty chat that uses the template (the chat is created when the first message is sent).</summary>
    public void NewChatFromTemplate(ChatTemplate t)
    {
        NewChat();
        TemplatePickerOpen = false;
        _draftTemplate = t.Copy();
        if (t.ProjectId != null && Projects.Any(p => p.Id == t.ProjectId)) DraftProjectId = t.ProjectId;
        if (t.PersonaId != null && Personas.Any(p => p.Id == t.PersonaId)) DraftPersonaId = t.PersonaId;
        var prov = Providers.FirstOrDefault(p => p.Id == t.ProviderId);
        if (prov != null)
        {
            if (!string.IsNullOrWhiteSpace(t.Model)) SelectProviderModel(prov.Id, t.Model);
            else
            {
                CurrentProviderId = prov.Id;
                CurrentModel = PickModel(prov);
                PersistSelection();
            }
        }
        Notify();
    }

    /// <summary>The template of the chat that is about to start (null when none).</summary>
    public ChatTemplate? DraftTemplate => _draftTemplate;

    /// <summary>Copies the draft template's settings into the chat that was just created.</summary>
    void ApplyTemplateDraft(Conversation? c)
    {
        if (c == null || _draftTemplate == null) return;
        ChatTags.Apply(_draftTemplate, c);
        _draftTemplate = null;
    }
}
