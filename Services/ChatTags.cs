using System.Text.Json;
using AiChat.Models;

namespace AiChat.Services;

/// <summary>A ready-made start for a new chat: persona, model, tools, memory, extra instruction, tags and project.</summary>
public class ChatTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "📋";
    public string? PersonaId { get; set; }
    public string? ProviderId { get; set; }
    public string Model { get; set; } = "";
    /// <summary>null = follow persona / global setting.</summary>
    public bool? Tools { get; set; }
    /// <summary>null = follow persona / global setting.</summary>
    public bool? Memory { get; set; }
    /// <summary>Extra system instruction of chats made from this template.</summary>
    public string Prompt { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public string? ProjectId { get; set; }

    public ChatTemplate Copy() => new()
    {
        Id = Id, Name = Name, Icon = Icon, PersonaId = PersonaId, ProviderId = ProviderId, Model = Model,
        Tools = Tools, Memory = Memory, Prompt = Prompt, Tags = Tags.ToList(), ProjectId = ProjectId
    };
}

/// <summary>Pure helpers for chat labels (tags) and chat templates.</summary>
public static class ChatTags
{
    public const int MaxLength = 24;
    public const int MaxPerChat = 8;

    /// <summary>Trims, collapses spaces, removes a leading '#' and cuts the length. Empty = invalid.</summary>
    public static string Normalize(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return "";
        var t = string.Join(" ", tag.Trim().TrimStart('#').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return t.Length > MaxLength ? t[..MaxLength].TrimEnd() : t;
    }

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.CurrentCultureIgnoreCase);

    public static bool Has(IEnumerable<string>? tags, string tag) => tags != null && tags.Any(t => Same(t, tag));

    /// <summary>Adds a tag (no duplicates, case-insensitive, at most <see cref="MaxPerChat"/>). Returns true when added.</summary>
    public static bool Add(List<string> tags, string? tag)
    {
        var t = Normalize(tag);
        if (t.Length == 0 || Has(tags, t) || tags.Count >= MaxPerChat) return false;
        tags.Add(t);
        return true;
    }

    public static bool Remove(List<string> tags, string tag) => tags.RemoveAll(t => Same(t, tag)) > 0;

    /// <summary>Every tag in use with its number of chats, most used first, then alphabetical.</summary>
    public static List<(string Tag, int Count)> All(IEnumerable<Conversation> convs)
    {
        var map = new Dictionary<string, (string Tag, int Count)>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var c in convs)
            foreach (var t in c.Tags.Where(x => !string.IsNullOrWhiteSpace(x)))
                map[t] = map.TryGetValue(t, out var e) ? (e.Tag, e.Count + 1) : (t, 1);
        return map.Values.OrderByDescending(x => x.Count).ThenBy(x => x.Tag, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>A chat passes when it has every selected tag (empty filter = everything passes).</summary>
    public static bool Matches(Conversation c, ICollection<string> selected) =>
        selected.Count == 0 || selected.All(s => Has(c.Tags, s));

    /// <summary>Stable hue (0–359) for a tag, so the same tag always has the same colour.</summary>
    public static int Hue(string tag)
    {
        unchecked
        {
            var h = 17;
            foreach (var ch in (tag ?? "").Trim().ToLowerInvariant()) h = h * 31 + ch;
            return (int)((uint)h % 360);
        }
    }

    /// <summary>Removes a tag from every chat (and returns how many chats changed).</summary>
    public static int RemoveEverywhere(IEnumerable<Conversation> convs, string tag)
    {
        var n = 0;
        foreach (var c in convs) if (Remove(c.Tags, tag)) n++;
        return n;
    }

    /// <summary>Renames a tag in every chat; merging into an existing tag is fine. Returns how many chats changed.</summary>
    public static int RenameEverywhere(IEnumerable<Conversation> convs, string from, string to)
    {
        var target = Normalize(to);
        if (target.Length == 0) return 0;
        var n = 0;
        foreach (var c in convs)
        {
            if (!Has(c.Tags, from)) continue;
            Remove(c.Tags, from);
            Add(c.Tags, target);
            n++;
        }
        return n;
    }

    // ---- templates

    /// <summary>Builds a template from an existing chat.</summary>
    public static ChatTemplate FromConversation(Conversation c, string name)
    {
        return new ChatTemplate
        {
            Name = string.IsNullOrWhiteSpace(name) ? c.Title : name.Trim(),
            PersonaId = c.PersonaId,
            ProviderId = c.ProviderId,
            Model = c.Model ?? "",
            Tools = c.Tools,
            Memory = c.Memory,
            Prompt = c.Gen?.SystemPrompt ?? "",
            Tags = c.Tags.ToList(),
            ProjectId = c.ProjectId
        };
    }

    /// <summary>Puts the template's settings into a new chat (ids that no longer exist are the caller's job).</summary>
    public static void Apply(ChatTemplate t, Conversation c)
    {
        foreach (var tag in t.Tags) Add(c.Tags, tag);
        c.Tools = t.Tools;
        c.Memory = t.Memory;
        if (!string.IsNullOrWhiteSpace(t.Prompt))
        {
            c.Gen ??= new GenSettings();
            c.Gen.SystemPrompt = t.Prompt;
        }
    }

    public static string ToJson(IEnumerable<ChatTemplate> list) => JsonSerializer.Serialize(list);

    public static List<ChatTemplate> FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            var l = JsonSerializer.Deserialize<List<ChatTemplate>>(json) ?? new();
            return l.Where(x => !string.IsNullOrWhiteSpace(x.Name)).ToList();
        }
        catch { return new(); }
    }
}
