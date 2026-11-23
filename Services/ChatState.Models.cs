using Microsoft.Maui.Storage;

namespace AiChat.Services;

/// <summary>Favourite models, quick model switch and a prompt for each model.</summary>
public partial class ChatState
{
    public ModelFav ModelFavs { get; private set; } = new();

    void LoadModelFav() => ModelFavs = ModelFav.FromJson(Preferences.Default.Get("model_fav", ""));

    void SaveModelFav() => Preferences.Default.Set("model_fav", ModelFavs.ToJson());

    public bool IsFavorite(string? providerId, string? model) => ModelFavs.IsFav(providerId, model);

    public void ToggleFavorite(string? providerId, string? model)
    {
        ModelFavs.Toggle(providerId, model);
        SaveModelFav();
        Notify();
    }

    public void MoveFavorite(string key, int delta)
    {
        ModelFavs.Move(key, delta);
        SaveModelFav();
        Notify();
    }

    public void SetModelPrompt(string? providerId, string? model, string? text)
    {
        ModelFavs.SetPrompt(providerId, model, text);
        SaveModelFav();
        Notify();
    }

    public string ModelPromptFor(string? providerId, string? model) => ModelFavs.PromptFor(providerId, model);

    public void SelectProviderModel(string providerId, string model)
    {
        if (Providers.All(p => p.Id != providerId)) return;
        CurrentProviderId = providerId;
        SelectModel(model);
    }

    // ------------------------------------------------------------ quick model switch
    public bool QuickModelOpen { get; private set; }
    public string QuickFilter { get; private set; } = "";

    public void OpenQuickModel()
    {
        if (Providers.Count == 0) return;
        QuickFilter = "";
        QuickModelOpen = true;
        Notify();
    }

    public void CloseQuickModel() { QuickModelOpen = false; Notify(); }

    public void SetQuickFilter(string text) { QuickFilter = text; Notify(); }

    public List<QuickItem> QuickItems() => ModelFavs.Search(Providers, ModelsFor, QuickFilter);
}
