using AiChat.Services;

namespace AiChat.Tests;

public class ConnectorSpecTests
{
    const string Minimal = """{ "request": { "path": "/x", "body": {"p":"{{prompt}}"} }, "response": { "type": "binary" } }""";

    [Fact]
    public void Parses_comments_and_trailing_commas()
    {
        var o = ConnectorSpec.TryParse("""
        {
          // comment
          "request": { "path": "/x", },
        }
        """, out var err);
        Assert.NotNull(o);
        Assert.Null(err);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[1,2]")]
    [InlineData("{ not json")]
    public void Bad_input_is_rejected(string json)
    {
        Assert.Null(ConnectorSpec.TryParse(json, out var err));
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Fact]
    public void Minimal_connector_is_valid() => Assert.Null(ConnectorSpec.Validate(Minimal));

    [Fact]
    public void Missing_request_is_reported() =>
        Assert.Contains("request", ConnectorSpec.Validate("""{ "response": {"type":"binary"} }""")!);

    [Fact]
    public void Missing_path_is_reported() =>
        Assert.Contains("path", ConnectorSpec.Validate("""{ "request": {}, "response": {"type":"binary"} }""")!);

    [Fact]
    public void Missing_response_is_reported() =>
        Assert.Contains("response", ConnectorSpec.Validate("""{ "request": {"path":"/x"} }""")!);

    [Fact]
    public void Bad_json_path_is_reported() =>
        Assert.NotNull(ConnectorSpec.Validate("""{ "request": {"path":"/x"}, "response": {"results":[{"from":"$.a[","as":"url"}]} }"""));

    [Fact]
    public void Job_requires_id_and_poll_path()
    {
        Assert.Contains("job.id", ConnectorSpec.Validate("""{ "request": {"path":"/x"}, "job": {} }""")!);
        Assert.Contains("poll", ConnectorSpec.Validate("""{ "request": {"path":"/x"}, "job": {"id":"$.id"} }""")!);
    }

    [Fact]
    public void Chat_requires_text_path()
    {
        Assert.Contains("textPath", ConnectorSpec.Validate("""{ "kind":"chat", "request": {"path":"/c"} }""")!);
        Assert.Null(ConnectorSpec.Validate("""{ "kind":"chat", "request": {"path":"/c"}, "stream": {"textPath":"$.t"} }"""));
    }

    [Fact]
    public void Chat_rejects_unknown_stream_format() =>
        Assert.Contains("format", ConnectorSpec.Validate("""{ "kind":"chat", "request": {"path":"/c"}, "stream": {"textPath":"$.t","format":"xml"} }""")!);

    const string WithInputs = """
    { "request": {"path":"/x"}, "response": {"type":"binary"},
      "inputs": [
        {"id":"steps","type":"slider","min":1,"max":50,"default":"20"},
        {"id":"hd","type":"bool","default":"true"},
        {"id":"style","type":"select","options":["a","b"],"default":"a","label":"Style"},
        {"id":"name"},
        {"label":"no id"}
      ] }
    """;

    [Fact]
    public void Inputs_are_parsed_and_invalid_ones_skipped()
    {
        var inputs = ConnectorSpec.Inputs(WithInputs);
        Assert.Equal(new[] { "steps", "hd", "style", "name" }, inputs.Select(i => i.Id));
        Assert.Equal("text", inputs.Single(i => i.Id == "name").Type);
        Assert.Equal("Style", inputs.Single(i => i.Id == "style").Label);
        Assert.Equal(50, inputs.Single(i => i.Id == "steps").Max);
        Assert.Equal(new[] { "a", "b" }, inputs.Single(i => i.Id == "style").Options);
    }

    [Fact]
    public void InputValue_uses_user_value_then_default_with_type()
    {
        var defs = ConnectorSpec.Inputs(WithInputs);
        var none = new Dictionary<string, string>();
        Assert.Equal(20L, ConnectorSpec.InputValue(defs, none, "steps")!.GetValue<long>());
        Assert.Equal(35L, ConnectorSpec.InputValue(defs, new Dictionary<string, string> { ["steps"] = "35" }, "steps")!.GetValue<long>());
        Assert.True(ConnectorSpec.InputValue(defs, none, "hd")!.GetValue<bool>());
        Assert.False(ConnectorSpec.InputValue(defs, new Dictionary<string, string> { ["hd"] = "false" }, "hd")!.GetValue<bool>());
        Assert.Equal("a", ConnectorSpec.InputValue(defs, none, "style")!.GetValue<string>());
        Assert.Null(ConnectorSpec.InputValue(defs, none, "name"));       // empty -> dropped from the request
        Assert.Null(ConnectorSpec.InputValue(defs, none, "unknown"));
    }

    [Fact]
    public void Controls_parse_and_bad_json_gives_empty()
    {
        Assert.Single(ConnectorSpec.ControlInputs("""[{"id":"t","type":"slider","default":"0.7"}]"""));
        Assert.Empty(ConnectorSpec.ControlInputs("not json"));
        Assert.Empty(ConnectorSpec.ControlInputs(""));
    }
}

public class TemplateTests
{
    public static IEnumerable<object[]> All() => ConnectorTemplates.All.Select(t => new object[] { t.Name });

    [Theory, MemberData(nameof(All))]
    public void Every_built_in_template_is_valid(string name)
    {
        var t = ConnectorTemplates.All.Single(x => x.Name == name);
        Assert.Null(ConnectorSpec.Validate(t.Json));
    }

    [Fact]
    public void Template_names_are_unique() =>
        Assert.Equal(ConnectorTemplates.All.Length, ConnectorTemplates.All.Select(t => t.Name).Distinct().Count());

    [Fact]
    public void Templates_have_help_text() =>
        Assert.All(ConnectorTemplates.All, t =>
            Assert.False(string.IsNullOrWhiteSpace(ConnectorSpec.TryParse(t.Json, out _)?["_help"]?.ToString()), t.Name));

    [Fact]
    public void Input_ids_are_unique_within_a_template() =>
        Assert.All(ConnectorTemplates.All, t =>
        {
            var ids = ConnectorSpec.Inputs(t.Json).Select(i => i.Id).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
        });
}
