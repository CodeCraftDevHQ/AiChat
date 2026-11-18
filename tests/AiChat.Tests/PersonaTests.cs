using System.Text.Json;
using AiChat.Models;

namespace AiChat.Tests;

public class PersonaTests
{
    [Fact]
    public void Layers_PersonaOverridesApi_ChatOverridesPersona()
    {
        var global = new GenSettings { Temperature = 1.0, MaxTokens = 100 };
        var api = new GenSettings { Temperature = 0.8 };
        var persona = new GenSettings { Temperature = 0.2, TopP = 0.5 };
        var chat = new GenSettings { TopP = 0.9 };
        var r = GenSettings.Resolve(global, api, persona, chat);
        Assert.Equal(0.2, r.Temperature);
        Assert.Equal(0.9, r.TopP);
        Assert.Equal(100, r.MaxTokens);
    }

    [Fact]
    public void Copy_IsDeepAndKeepsFields()
    {
        var p = new Persona { Name = "x", Icon = "🔎", ProviderId = "p1", Model = "m", Tools = true, Gen = new GenSettings { Temperature = 0.3, SystemPrompt = "hi" } };
        var c = p.Copy();
        c.Gen.SystemPrompt = "changed";
        Assert.Equal("hi", p.Gen.SystemPrompt);
        Assert.Equal("p1", c.ProviderId);
        Assert.True(c.Tools);
        Assert.NotEqual(p.Id, c.Id);
    }

    [Fact]
    public void Json_RoundTrip_KeepsNullTools()
    {
        var list = Persona.Samples();
        var back = JsonSerializer.Deserialize<List<Persona>>(JsonSerializer.Serialize(list))!;
        Assert.Equal(list.Count, back.Count);
        Assert.Null(back.First(x => x.Name == "Programmer").Tools);
        Assert.False(back.First(x => x.Name == "Writer").Tools);
    }
}
