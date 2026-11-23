using System.Text.Json;
using AiChat.Models;

namespace AiChat.Services;

public record QuickItem(ProviderConfig Provider, string Model, string Section);

/// <summary>Favourite models, recently used models and a prompt for single models.</summary>
public class ModelFav
{
    public const int MaxRecent = 8;

    /// <summary>"providerId|model" in the order the user wants them.</summary>
    public List<string> Favs { get; set; } = new();
    public List<string> Recent { get; set; } = new();
    /// <summary>"providerId|model" → system instruction used whenever that model answers.</summary>
    public Dictionary<string, string> Prompts { get; set; } = new();

    public static string Key(string? providerId, string? model) => (providerId ?? "") + "|" + (model ?? "");

    public static (string ProviderId, string Model) Split(string key)
    {
        var i = key.IndexOf('|');
        return i < 0 ? ("", key) : (key[..i], key[(i + 1)..]);
    }

    public bool IsFav(string? providerId, string? model) => Favs.Contains(Key(providerId, model));

    /// <summary>Adds or removes; returns true when it is a favourite now.</summary>
    public bool Toggle(string? providerId, string? model)
    {
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(model)) return false;
        var k = Key(providerId, model);
        if (Favs.Remove(k)) return false;
        Favs.Add(k);
        return true;
    }

    public void Move(string key, int delta)
    {
        var i = Favs.IndexOf(key);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= Favs.Count) return;
        (Favs[i], Favs[j]) = (Favs[j], Favs[i]);
    }

    /// <summary>The model was used: it goes to the front of the recent list.</summary>
    public void Touch(string? providerId, string? model)
    {
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(model)) return;
        var k = Key(providerId, model);
        Recent.Remove(k);
        Recent.Insert(0, k);
        if (Recent.Count > MaxRecent) Recent.RemoveRange(MaxRecent, Recent.Count - MaxRecent);
    }

    public void SetPrompt(string? providerId, string? model, string? text)
    {
        var k = Key(providerId, model);
        if (string.IsNullOrWhiteSpace(text)) Prompts.Remove(k);
        else Prompts[k] = text.Trim();
    }

    public string PromptFor(string? providerId, string? model) =>
        !string.IsNullOrEmpty(model) && Prompts.TryGetValue(Key(providerId, model), out var p) ? p : "";

    /// <summary>Forgets what belongs to an API that was deleted.</summary>
    public void DropProvider(string providerId)
    {
        bool Of(string k) => Split(k).ProviderId == providerId;
        Favs.RemoveAll(Of);
        Recent.RemoveAll(Of);
        foreach (var k in Prompts.Keys.Where(Of).ToList()) Prompts.Remove(k);
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    public static ModelFav FromJson(string? json)
    {
        try { return string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<ModelFav>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    /// <summary>List for the quick switcher: favourites, then recent, then (when searching or as a short list) the rest.</summary>
    public List<QuickItem> Search(IReadOnlyList<ProviderConfig> providers, Func<ProviderConfig, IEnumerable<string>> modelsOf, string? query, int limit = 60)
    {
        var words = (query ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        bool Match(ProviderConfig p, string m) =>
            words.All(w => m.Contains(w, StringComparison.OrdinalIgnoreCase) || p.Name.Contains(w, StringComparison.OrdinalIgnoreCase));

        var res = new List<QuickItem>();
        var seen = new HashSet<string>();
        void Add(string key, string section)
        {
            var (pid, model) = Split(key);
            var p = providers.FirstOrDefault(x => x.Id == pid);
            if (p == null || model.Length == 0 || !Match(p, model) || !seen.Add(key)) return;
            res.Add(new QuickItem(p, model, section));
        }
        foreach (var k in Favs) Add(k, "fav");
        foreach (var k in Recent) Add(k, "recent");
        if (words.Length > 0 || res.Count < 12)
            foreach (var p in providers)
                foreach (var m in modelsOf(p).Distinct())
                {
                    if (res.Count >= limit) return res;
                    Add(Key(p.Id, m), "all");
                }
        return res.Take(limit).ToList();
    }
}
