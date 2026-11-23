using AiChat.Services;
using Xunit;

namespace AiChat.Tests;

public class UiStyleTests
{
    [Fact]
    public void Colours_are_normalized()
    {
        Assert.Equal("#aabbcc", UiStyleHelper.NormalizeHex("ABC"));
        Assert.Equal("#10a37f", UiStyleHelper.NormalizeHex(" #10A37F "));
        Assert.Null(UiStyleHelper.NormalizeHex("zzz"));
        Assert.Null(UiStyleHelper.NormalizeHex(""));
        Assert.Null(UiStyleHelper.NormalizeHex(null));
        Assert.Null(UiStyleHelper.NormalizeHex("#12345"));
    }

    [Fact]
    public void Text_colour_follows_the_background()
    {
        Assert.Equal("#111111", UiStyleHelper.OnColor("#ffffff"));
        Assert.Equal("#ffffff", UiStyleHelper.OnColor("#000000"));
        Assert.Equal("#ffffff", UiStyleHelper.OnColor("#10a37f"));
        Assert.Equal("#111111", UiStyleHelper.OnColor("#ffd400"));
    }

    [Fact]
    public void Default_style_removes_the_custom_colours()
    {
        var v = UiStyleHelper.Vars(new UiStyle());
        Assert.Null(v["--accent"]);
        Assert.Null(v["--user-bg"]);
        Assert.Equal("22px", v["--bubble-r"]);
        Assert.Equal("10px", v["--bubble-py"]);
        Assert.Equal("18px", v["--bubble-px"]);
        Assert.Equal("18px", v["--msg-gap"]);
        Assert.Equal("85%", v["--bubble-max"]);
    }

    [Fact]
    public void Custom_style_gives_variables()
    {
        var v = UiStyleHelper.Vars(new UiStyle { Accent = "#112233", UserBubble = "#ffee00", Pad = "roomy", Radius = 4 });
        Assert.Equal("#112233", v["--accent"]);
        Assert.Equal("#ffffff", v["--accent-fg"]);
        Assert.Equal("#ffee00", v["--user-bg"]);
        Assert.Equal("#111111", v["--user-fg"]);
        Assert.Equal("14px", v["--bubble-py"]);
        Assert.Equal("22px", v["--bubble-px"]);
        Assert.Equal("4px", v["--bubble-r"]);
        Assert.Contains("--accent", UiStyleHelper.VarsJson(new UiStyle()));
    }

    [Fact]
    public void Values_are_kept_in_range()
    {
        var s = UiStyleHelper.Normalize(new UiStyle { Radius = 99, Gap = 1, BubbleMax = 10, Pad = "x", Accent = "not a colour" });
        Assert.Equal((32, 6, 50, "normal", ""), (s.Radius, s.Gap, s.BubbleMax, s.Pad, s.Accent));
    }

    [Fact]
    public void Themes_are_validated()
    {
        Assert.Equal("nord", UiStyleHelper.NormalizeTheme("nord"));
        Assert.Equal("system", UiStyleHelper.NormalizeTheme("zzz"));
        Assert.Equal("system", UiStyleHelper.NormalizeTheme(null));
        Assert.True(UiStyleHelper.IsDarkTheme("amoled"));
        Assert.False(UiStyleHelper.IsDarkTheme("sepia"));
    }

    [Theory]
    [InlineData("ctrl+shift+o", "Ctrl+Shift+O")]
    [InlineData("Alt+Ctrl+x", "Ctrl+Alt+X")]
    [InlineData("cmd+b", "Ctrl+B")]
    [InlineData("Ctrl+,", "Ctrl+,")]
    [InlineData("Ctrl+enter", "Ctrl+Enter")]
    [InlineData("F5", "F5")]
    [InlineData("Alt+f12", "Alt+F12")]
    [InlineData("Ctrl+Esc", "Ctrl+Escape")]
    public void Combinations_are_canonical(string input, string expected) => Assert.Equal(expected, KeyBindings.Normalize(input));

    [Theory]
    [InlineData("O")]
    [InlineData("Shift+O")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Foo")]
    [InlineData("")]
    [InlineData("Ctrl++")]
    [InlineData("Win+K")]
    public void Unusable_combinations_are_refused(string input) => Assert.Null(KeyBindings.Normalize(input));

    [Fact]
    public void Key_presses_become_combinations()
    {
        Assert.Equal("Ctrl+Shift+O", KeyBindings.FromEvent("O", "KeyO", true, false, true, false));
        Assert.Equal("Ctrl+Shift+O", KeyBindings.FromEvent("پ", "KeyO", true, false, true, false)); // Persian layout
        Assert.Equal("Ctrl+,", KeyBindings.FromEvent(",", "Comma", true, false, false, false));
        Assert.Equal("Ctrl+B", KeyBindings.FromEvent("b", "KeyB", false, false, false, true));    // Cmd = Ctrl
        Assert.Equal("Ctrl+1", KeyBindings.FromEvent("1", "Digit1", true, false, false, false));
        Assert.Equal("Ctrl+Space", KeyBindings.FromEvent(" ", "Space", true, false, false, false));
        Assert.Equal("Ctrl+Enter", KeyBindings.FromEvent("Enter", "Enter", true, false, false, false));
        Assert.Equal("F2", KeyBindings.FromEvent("F2", "F2", false, false, false, false));
        Assert.Null(KeyBindings.FromEvent("a", "KeyA", false, false, false, false));       // would be typing
        Assert.Null(KeyBindings.FromEvent("Control", "ControlLeft", true, false, false, false));
    }

    [Fact]
    public void Default_shortcuts_are_valid_and_distinct()
    {
        var combos = KeyBindings.Actions.Select(a => a.Default).ToList();
        Assert.Equal(combos.Count, combos.Distinct().Count());
        foreach (var a in KeyBindings.Actions)
        {
            Assert.Equal(a.Default, KeyBindings.Normalize(a.Default));
            Assert.Null(KeyBindings.Reserved(a.Default));
        }
    }

    [Fact]
    public void Reserved_combinations_are_known()
    {
        Assert.NotNull(KeyBindings.Reserved("Ctrl+C"));
        Assert.NotNull(KeyBindings.Reserved("Ctrl+F"));
        Assert.Null(KeyBindings.Reserved("Ctrl+B"));
    }

    [Fact]
    public void User_changes_are_merged_with_defaults()
    {
        Assert.Equal(KeyBindings.Defaults(), KeyBindings.Parse(null));
        Assert.Equal(KeyBindings.Defaults(), KeyBindings.Parse("not json"));

        var m = KeyBindings.Parse("{\"sidebar\":\"ctrl+shift+b\",\"theme\":\"\",\"nothing\":\"Ctrl+Q\"}");
        Assert.Equal("Ctrl+Shift+B", m["sidebar"]);
        Assert.Equal("", m["theme"]);
        Assert.False(m.ContainsKey("nothing"));

        // reserved / unusable values are ignored
        var r = KeyBindings.Parse("{\"stop\":\"Ctrl+C\",\"focus\":\"x\"}");
        Assert.Equal(KeyBindings.Defaults()["stop"], r["stop"]);
        Assert.Equal(KeyBindings.Defaults()["focus"], r["focus"]);

        // one combination for two actions: the earlier action keeps it
        var c = KeyBindings.Parse("{\"stop\":\"Ctrl+B\"}");
        Assert.Equal("Ctrl+B", c["sidebar"]);
        Assert.Equal("", c["stop"]);
    }

    [Fact]
    public void Only_differences_are_saved()
    {
        Assert.Equal("{}", KeyBindings.Serialize(KeyBindings.Defaults()));
        var m = KeyBindings.Defaults();
        m["sidebar"] = "Ctrl+Shift+B";
        m["theme"] = "";
        var json = KeyBindings.Serialize(m);
        Assert.Contains("sidebar", json);
        Assert.DoesNotContain("stop", json);
        Assert.Equal(m, KeyBindings.Parse(json));
    }

    [Fact]
    public void Conflicts_are_found()
    {
        var m = KeyBindings.Defaults();
        Assert.Equal("sidebar", KeyBindings.Conflict(m, "stop", "Ctrl+B"));
        Assert.Null(KeyBindings.Conflict(m, "sidebar", "Ctrl+B"));
        Assert.Null(KeyBindings.Conflict(m, "stop", "Ctrl+Shift+B"));
    }
}
