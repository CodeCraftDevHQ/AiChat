using AiChat.Services;
using static AiChat.Tests.Helpers;

namespace AiChat.Tests;

public class JPathTests
{
    const string Doc = """{"a":{"b":[{"id":"x"},{"id":"y"}],"n":5},"list":[1,2,3],"s":"hi"}""";

    [Theory]
    [InlineData("$.a.n", "5")]
    [InlineData("$.s", "hi")]
    [InlineData("$.a.b[0].id", "x")]
    [InlineData("$.a.b[1].id", "y")]
    [InlineData("$.a.b[-1].id", "y")]
    [InlineData("$['a']['n']", "5")]
    [InlineData("a.n", "5")]
    public void Selects_single_values(string path, string expected)
    {
        var r = JPath.Select(J(Doc), path);
        Assert.Single(r);
        Assert.Equal(expected, JPath.Text(r[0]));
    }

    [Fact]
    public void Wildcard_over_array_returns_all_items()
    {
        var r = JPath.Select(J(Doc), "$.a.b[*].id");
        Assert.Equal(new[] { "x", "y" }, r.Select(JPath.Text));
    }

    [Fact]
    public void Root_array_wildcard()
    {
        var r = JPath.Select(J("""[{"id":"a"},{"id":"b"}]"""), "$[*].id");
        Assert.Equal(new[] { "a", "b" }, r.Select(JPath.Text));
    }

    [Fact]
    public void Dot_star_over_object_values()
    {
        var r = JPath.Select(J("""{"x":{"v":1},"y":{"v":2}}"""), "$.*.v");
        Assert.Equal(new[] { "1", "2" }, r.Select(JPath.Text));
    }

    [Theory]
    [InlineData("$.missing")]
    [InlineData("$.a.b[9]")]
    [InlineData("$.list.x")]
    public void Missing_paths_return_nothing(string path) =>
        Assert.Empty(JPath.Select(J(Doc), path));

    [Theory]
    [InlineData("$.a[")]
    [InlineData("$.a[x]")]
    [InlineData("$..a")]
    public void Invalid_paths_throw(string path) =>
        Assert.Throws<FormatException>(() => JPath.Check(path));

    [Fact]
    public void Text_of_null_is_empty_and_of_object_is_json()
    {
        Assert.Equal("", JPath.Text(null));
        Assert.Equal("""{"a":1}""", JPath.Text(J("""{"a":1}""")));
    }
}
