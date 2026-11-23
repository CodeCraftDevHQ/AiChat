using AiChat.Models;
using AiChat.Services;
using Xunit;

namespace AiChat.Tests;

public class ModelFavTests
{
    static List<ProviderConfig> Provs() => new()
    {
        new ProviderConfig { Id = "a", Name = "OpenAI", Models = new() { "gpt-4o", "gpt-mini" } },
        new ProviderConfig { Id = "b", Name = "Local", Models = new() { "llama3", "qwen" } }
    };

    static IEnumerable<string> Models(ProviderConfig p) => p.Models;

    [Fact]
    public void Favourites_toggle_and_move()
    {
        var f = new ModelFav();
        Assert.True(f.Toggle("a", "gpt-4o"));
        Assert.True(f.Toggle("b", "qwen"));
        Assert.True(f.IsFav("a", "gpt-4o"));
        f.Move(ModelFav.Key("b", "qwen"), -1);
        Assert.Equal(new[] { "b|qwen", "a|gpt-4o" }, f.Favs.ToArray());
        f.Move(ModelFav.Key("b", "qwen"), -1);   // already first: nothing happens
        Assert.Equal("b|qwen", f.Favs[0]);
        Assert.False(f.Toggle("a", "gpt-4o"));
        Assert.False(f.IsFav("a", "gpt-4o"));
        Assert.False(f.Toggle("", "x"));
        Assert.False(f.Toggle("a", " "));
    }

    [Fact]
    public void Keys_with_a_slash_or_bar_in_the_model_survive()
    {
        var k = ModelFav.Key("p1", "openai/gpt|4");
        Assert.Equal(("p1", "openai/gpt|4"), ModelFav.Split(k));
    }

    [Fact]
    public void Recent_list_is_short_and_newest_first()
    {
        var f = new ModelFav();
        for (var i = 0; i < 12; i++) f.Touch("a", "m" + i);
        Assert.Equal(ModelFav.MaxRecent, f.Recent.Count);
        Assert.Equal("a|m11", f.Recent[0]);
        f.Touch("a", "m5");
        Assert.Equal("a|m5", f.Recent[0]);
        Assert.Equal(f.Recent.Count, f.Recent.Distinct().Count());
        f.Touch(null, "x");
        Assert.DoesNotContain(f.Recent, r => r.EndsWith("|x"));
    }

    [Fact]
    public void Model_prompts_are_set_read_and_removed()
    {
        var f = new ModelFav();
        f.SetPrompt("a", "gpt-mini", "  Answer briefly.  ");
        Assert.Equal("Answer briefly.", f.PromptFor("a", "gpt-mini"));
        Assert.Equal("", f.PromptFor("a", "gpt-4o"));
        Assert.Equal("", f.PromptFor("a", null));
        f.SetPrompt("a", "gpt-mini", "");
        Assert.Empty(f.Prompts);
    }

    [Fact]
    public void Deleting_an_api_removes_its_models()
    {
        var f = new ModelFav();
        f.Toggle("a", "x"); f.Toggle("b", "y"); f.Touch("a", "x"); f.SetPrompt("a", "x", "p"); f.SetPrompt("b", "y", "q");
        f.DropProvider("a");
        Assert.Equal(new[] { "b|y" }, f.Favs.ToArray());
        Assert.Empty(f.Recent);
        Assert.Single(f.Prompts);
    }

    [Fact]
    public void Json_roundtrip_and_bad_json()
    {
        var f = new ModelFav();
        f.Toggle("a", "gpt-4o"); f.Touch("b", "qwen"); f.SetPrompt("a", "gpt-4o", "hi");
        var back = ModelFav.FromJson(f.ToJson());
        Assert.Equal(f.Favs, back.Favs);
        Assert.Equal(f.Recent, back.Recent);
        Assert.Equal("hi", back.PromptFor("a", "gpt-4o"));
        Assert.Empty(ModelFav.FromJson("{{bad").Favs);
        Assert.Empty(ModelFav.FromJson(null).Favs);
    }

    [Fact]
    public void Quick_list_puts_favourites_then_recent_first()
    {
        var f = new ModelFav();
        f.Toggle("b", "qwen");
        f.Touch("a", "gpt-mini");
        f.Touch("b", "qwen");     // already a favourite: listed once
        var list = f.Search(Provs(), Models, "");
        Assert.Equal(("b", "qwen", "fav"), (list[0].Provider.Id, list[0].Model, list[0].Section));
        Assert.Equal(("a", "gpt-mini", "recent"), (list[1].Provider.Id, list[1].Model, list[1].Section));
        Assert.Equal(list.Count, list.Select(i => i.Provider.Id + i.Model).Distinct().Count());
        Assert.Contains(list, i => i.Model == "llama3" && i.Section == "all");   // short list: the rest follows
    }

    [Fact]
    public void Quick_search_matches_words_in_model_or_api_name()
    {
        var f = new ModelFav();
        var byModel = f.Search(Provs(), Models, "llama");
        Assert.Equal(new[] { "llama3" }, byModel.Select(i => i.Model).ToArray());
        var byApi = f.Search(Provs(), Models, "local qw");
        Assert.Equal(new[] { "qwen" }, byApi.Select(i => i.Model).ToArray());
        Assert.Empty(f.Search(Provs(), Models, "zzz"));
        Assert.Equal(2, f.Search(Provs(), Models, "gpt").Count);
    }

    [Fact]
    public void Quick_list_skips_models_of_deleted_apis_and_respects_the_limit()
    {
        var f = new ModelFav();
        f.Toggle("gone", "x");
        Assert.DoesNotContain(f.Search(Provs(), Models, ""), i => i.Model == "x");

        var big = new List<ProviderConfig> { new() { Id = "z", Name = "Big", Models = Enumerable.Range(0, 300).Select(i => "m" + i).ToList() } };
        Assert.Equal(60, f.Search(big, Models, "m").Count);
        Assert.Equal(5, f.Search(big, Models, "m", 5).Count);
    }
}
