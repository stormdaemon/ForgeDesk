using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Detection.Parsing;

namespace ForgeDesk.Core.Tests.Detection;

public class ParsingTests
{
    [Fact]
    public void Toml_reads_tables_keys_strings_and_multiline_arrays()
    {
        var toml = TomlLite.Parse(""""
            title = "root" # comment
            [package]
            name = 'demo'
            description = """
            Multi-line # not a comment
            text
            """

            [dependencies]
            serde = { version = "1", features = ["derive"] }
            tokio.workspace = true

            [target.'cfg(windows)'.dependencies]
            winapi = "0.3"

            [[bin]]
            name = "cli"

            [workspace]
            members = [
              "a", # first
              'b',
            ]
            """".ReplaceLineEndings("\r\n"));

        toml.GetString("", "title").Should().Be("root");
        toml.GetString("package", "name").Should().Be("demo");
        toml.GetString("package", "description").Should().Contain("Multi-line # not a comment");
        toml.KeysOf("dependencies").Should().Equal("serde", "tokio");
        toml.HasTable("target.cfg(windows).dependencies").Should().BeTrue();
        toml.HasTable("bin").Should().BeTrue();
        toml.GetStringArray("workspace", "members").Should().Equal("a", "b");
        toml.HasTableOrChild("target").Should().BeTrue();
        toml.GetString("package", "missing").Should().BeNull();
    }

    [Fact]
    public void Toml_tolerates_garbage_lines()
    {
        var toml = TomlLite.Parse("[unterminated\nkey without value\n= orphan\nok = 1\n");

        toml.GetString("", "ok").Should().Be("1");
    }

    [Fact]
    public void Yaml_outline_lists_child_keys_and_skips_block_scalars()
    {
        var lines = YamlOutline.Read("""
            name: app # trailing comment
            dependencies:
              flutter:
                sdk: flutter
              "http": ^1.2.0
            script: |
              not: a key
              - not an item
            list:
            - one
            - two
            """);

        YamlOutline.TopLevelScalar(lines, "name").Should().Be("app");
        YamlOutline.ChildKeys(lines, "dependencies").Should().Equal("flutter", "http");
        YamlOutline.FindTopLevel(lines, "not").Should().Be(-1);
        var listPosition = YamlOutline.FindTopLevel(lines, "list");
        YamlOutline.Children(lines, listPosition).Select(l => l.Value).Should().Equal("one", "two");
    }

    [Theory]
    [InlineData("dev", CommandCategory.Dev)]
    [InlineData("start:prod", CommandCategory.Dev)]
    [InlineData("serve", CommandCategory.Dev)]
    [InlineData("watch", CommandCategory.Dev)]
    [InlineData("build", CommandCategory.Build)]
    [InlineData("build:prod", CommandCategory.Build)]
    [InlineData("compile", CommandCategory.Build)]
    [InlineData("test", CommandCategory.Test)]
    [InlineData("test:unit", CommandCategory.Test)]
    [InlineData("e2e", CommandCategory.Test)]
    [InlineData("coverage", CommandCategory.Test)]
    [InlineData("lint", CommandCategory.Lint)]
    [InlineData("lint:fix", CommandCategory.Lint)]
    [InlineData("typecheck", CommandCategory.Lint)]
    [InlineData("type-check", CommandCategory.Lint)]
    [InlineData("check", CommandCategory.Lint)]
    [InlineData("format", CommandCategory.Format)]
    [InlineData("prettier", CommandCategory.Format)]
    [InlineData("fmt", CommandCategory.Format)]
    [InlineData("package", CommandCategory.Package)]
    [InlineData("pack", CommandCategory.Package)]
    [InlineData("dist", CommandCategory.Package)]
    [InlineData("make", CommandCategory.Package)]
    [InlineData("electron:build", CommandCategory.Package)]
    [InlineData("clean", CommandCategory.Clean)]
    [InlineData("cache:clear", CommandCategory.Clean)]
    [InlineData("deploy", CommandCategory.Deploy)]
    [InlineData("release", CommandCategory.Deploy)]
    [InlineData("publish", CommandCategory.Deploy)]
    [InlineData("setup", CommandCategory.Install)]
    [InlineData("docker:build", CommandCategory.Build)]
    [InlineData("web:dev", CommandCategory.Dev)]
    [InlineData("preview", CommandCategory.Run)]
    [InlineData("db:migrate", CommandCategory.Run)]
    [InlineData("", CommandCategory.Run)]
    public void Command_categories_come_from_names(string name, CommandCategory expected) =>
        CommandCategorizer.FromName(name).Should().Be(expected);

    [Theory]
    [InlineData("build", "build")]
    [InlineData("build:prod", "build:prod")]
    [InlineData("my script", "\"my script\"")]
    [InlineData("a&b", "\"a&b\"")]
    [InlineData("", "\"\"")]
    public void Arguments_are_quoted_only_when_needed(string argument, string expected) =>
        CommandLines.Quote(argument).Should().Be(expected);
}
