using ForgeDesk.Core.Analysis.Dependencies;

namespace ForgeDesk.Core.Tests.Analysis.Dependencies;

public class TomlLiteTests
{
    [Fact]
    public void Reads_tables_keys_and_raw_values()
    {
        var entries = TomlLite.Parse(
            """
            # comment
            name = "demo" # trailing comment
            [dependencies]
            serde = { version = "1.0", features = ["derive"] }
            "quoted.key" = '2'

            [target.'cfg(windows)'.dependencies]
            winapi = "0.3"

            [[bin]]
            name = "tool"
            """);

        entries.Select(e => (e.TableName, string.Join('|', e.Key), e.Value)).Should().Equal(
            ("", "name", "\"demo\""),
            ("dependencies", "serde", "{ version = \"1.0\", features = [\"derive\"] }"),
            ("dependencies", "quoted.key", "'2'"),
            ("target.cfg(windows).dependencies", "winapi", "\"0.3\""),
            ("bin", "name", "\"tool\""));
        entries[3].Table.Should().Equal("target", "cfg(windows)", "dependencies");
    }

    [Fact]
    public void Joins_multi_line_arrays_and_skips_multi_line_strings()
    {
        var entries = TomlLite.Parse(
            """"
            [project]
            description = """
            dependencies = ["not", "real"]
            """
            dependencies = [
                "requests>=2", # http
                "rich",
            ]
            """");

        var dependencies = entries.Single(e => e.Key[0] == "dependencies");
        TomlLite.GetStringArray(dependencies.Value).Should().Equal("requests>=2", "rich");
    }

    [Fact]
    public void Splits_dotted_keys_with_quotes()
    {
        TomlLite.SplitKey("a.'b.c'.d").Should().Equal("a", "b.c", "d");
        TomlLite.SplitKey(" serde . workspace ").Should().Equal("serde", "workspace");
    }

    [Theory]
    [InlineData("\"1.0\"", "1.0")]
    [InlineData("'1.0'", "1.0")]
    [InlineData("\"a \\\"quoted\\\" word\"", "a \"quoted\" word")]
    [InlineData("\"C:\\\\path\"", @"C:\path")]
    public void Decodes_strings(string raw, string expected)
    {
        TomlLite.TryGetString(raw, out var value).Should().BeTrue();
        value.Should().Be(expected);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("42")]
    [InlineData("{ version = \"1\" }")]
    [InlineData("\"unterminated")]
    public void Rejects_non_strings(string raw) => TomlLite.TryGetString(raw, out _).Should().BeFalse();

    [Fact]
    public void Reads_inline_tables_with_dotted_keys()
    {
        var table = TomlLite.GetInlineTable("{ module = \"a:b\", version.ref = \"kotlin\", features = [\"x\", \"y\"] }");

        table.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["module"] = "\"a:b\"",
            ["version.ref"] = "\"kotlin\"",
            ["features"] = "[\"x\", \"y\"]",
        });
    }

    [Fact]
    public void Tolerates_garbage()
    {
        var entries = TomlLite.Parse("[unclosed\n= no key\njust text\nkey = [1, 2");

        entries.Should().ContainSingle().Which.Key.Should().Equal("key");
    }
}
