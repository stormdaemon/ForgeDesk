using ForgeDesk.App.Controls;
using ForgeDesk.App.Controls.Code;
using ForgeDesk.App.Theming;
using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Runs;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Tests.AppLogic;

public class StatusKindsTests
{
    [Theory]
    [InlineData(RunStatus.Running, StatusKind.Running)]
    [InlineData(RunStatus.Succeeded, StatusKind.Success)]
    [InlineData(RunStatus.Failed, StatusKind.Danger)]
    [InlineData(RunStatus.Interrupted, StatusKind.Warning)]
    [InlineData(RunStatus.Cancelled, StatusKind.Neutral)]
    [InlineData(RunStatus.Queued, StatusKind.Neutral)]
    public void Run_statuses_map(RunStatus status, StatusKind expected) => StatusKinds.From(status).Should().Be(expected);

    [Theory]
    [InlineData(CiState.Success, StatusKind.Success)]
    [InlineData(CiState.Failure, StatusKind.Danger)]
    [InlineData(CiState.Running, StatusKind.Running)]
    [InlineData(CiState.Queued, StatusKind.Warning)]
    [InlineData(CiState.None, StatusKind.Neutral)]
    [InlineData(CiState.Unknown, StatusKind.Neutral)]
    public void Ci_states_map(CiState state, StatusKind expected) => StatusKinds.From(state).Should().Be(expected);

    [Fact]
    public void Boxed_values_of_every_supported_type_map()
    {
        StatusKinds.From((object)NotificationSeverity.Error).Should().Be(StatusKind.Danger);
        StatusKinds.From((object)ActivityOutcome.Success).Should().Be(StatusKind.Success);
        StatusKinds.From((object)AttentionLevel.Critical).Should().Be(StatusKind.Danger);
        StatusKinds.From((object)StatusKind.Info).Should().Be(StatusKind.Info);
        StatusKinds.From(null).Should().Be(StatusKind.Neutral);
        StatusKinds.From(42).Should().Be(StatusKind.Neutral);
    }

    [Theory]
    [InlineData("success", StatusKind.Success)]
    [InlineData("Danger", StatusKind.Danger)]
    [InlineData("failed", StatusKind.Danger)]
    [InlineData("pending", StatusKind.Warning)]
    [InlineData("whatever", StatusKind.Neutral)]
    [InlineData("", StatusKind.Neutral)]
    public void Names_map(string name, StatusKind expected) => StatusKinds.FromName(name).Should().Be(expected);

    [Fact]
    public void Ci_labels_are_short_and_explained()
    {
        foreach (var state in Enum.GetValues<CiState>())
        {
            CiText.Label(state).Should().NotBeNullOrWhiteSpace();
            CiText.Label(state).Length.Should().BeLessThanOrEqualTo(10);
            CiText.Description(state).Should().EndWith(".");
        }

        CiText.Label(CiState.Success).Should().Be("Passing");
        CiText.Label(CiState.Failure).Should().Be("Failing");
    }
}

public class AvatarTextTests
{
    [Theory]
    [InlineData("ForgeDesk", "FD")]
    [InlineData("forge-desk", "FD")]
    [InlineData("forge_desk.app", "FD")]
    [InlineData("my  api server", "MA")]
    [InlineData("api", "A")]
    [InlineData("x", "X")]
    [InlineData("3d-engine", "3E")]
    [InlineData("éclair café", "ÉC")]
    [InlineData("   ", "?")]
    [InlineData(null, "?")]
    [InlineData("---", "?")]
    public void Initials(string? name, string expected) => AvatarText.Initials(name).Should().Be(expected);

    [Fact]
    public void Colors_are_stable_and_case_insensitive()
    {
        AvatarText.ColorFor("ForgeDesk").Should().Be(AvatarText.ColorFor("forgedesk"));
        AvatarText.ColorFor("ForgeDesk").Should().Be(AvatarText.ColorFor(" ForgeDesk "));
        AvatarText.ColorFor("ForgeDesk").A.Should().Be(255);
    }

    [Fact]
    public void Colors_vary_between_projects()
    {
        var names = Enumerable.Range(0, 40).Select(i => $"project-{i}");

        names.Select(AvatarText.ColorFor).Distinct().Count().Should().BeGreaterThan(5);
    }

    [Fact]
    public void Palette_keeps_white_initials_readable()
    {
        var names = Enumerable.Range(0, 200).Select(i => $"repo{i}");

        foreach (var color in names.Select(AvatarText.ColorFor).Distinct())
        {
            ColorMath.ContrastRatio(color, Argb.Opaque(255, 255, 255)).Should().BeGreaterThanOrEqualTo(3.0, color.ToString());
        }
    }
}

public class KeyHintParserTests
{
    [Theory]
    [InlineData("Ctrl+K", new[] { "Ctrl", "K" })]
    [InlineData("Ctrl+Shift+P", new[] { "Ctrl", "Shift", "P" })]
    [InlineData("ctrl+shift+f", new[] { "Ctrl", "Shift", "F" })]
    [InlineData("Ctrl++", new[] { "Ctrl", "+" })]
    [InlineData("+", new[] { "+" })]
    [InlineData("Ctrl + `", new[] { "Ctrl", "`" })]
    [InlineData("Alt+Left", new[] { "Alt", "←" })]
    [InlineData("F5", new[] { "F5" })]
    [InlineData("Escape", new[] { "Esc" })]
    [InlineData("Ctrl+Enter", new[] { "Ctrl", "Enter" })]
    public void Splits_shortcuts_into_keycaps(string keys, string[] expected) =>
        KeyHintParser.Parse(keys).Should().Equal(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_shortcut_has_no_keys(string? keys) => KeyHintParser.Parse(keys).Should().BeEmpty();
}

public class ErrorInfoTextTests
{
    [Fact]
    public void Includes_every_part_of_the_error()
    {
        var error = new ErrorInfo(ErrorKind.NonFastForward, "Push rejected", "The remote has new commits.", "Pull first, then push again.",
            "! [rejected] main -> main (fetch first)\n");

        var text = ErrorInfoText.Format(error);

        text.Should().ContainAll("Push rejected", "The remote has new commits.", "Pull first, then push again.", "NonFastForward",
            "! [rejected] main -> main (fetch first)");
        text.Should().EndWith(Environment.NewLine);
    }

    [Fact]
    public void Omits_missing_hint_and_detail()
    {
        var text = ErrorInfoText.Format(new ErrorInfo(ErrorKind.Timeout, "Timed out", "Git did not answer."));

        text.Should().Be($"Timed out{Environment.NewLine}Git did not answer.{Environment.NewLine}{Environment.NewLine}Kind: Timeout{Environment.NewLine}");
    }
}

public class SyntaxLanguageMapTests
{
    [Theory]
    [InlineData("Program.cs", "C#")]
    [InlineData("src/app.TSX", "JavaScript")]
    [InlineData("package.json", "Json")]
    [InlineData("tsconfig.jsonc", "Json")]
    [InlineData("Directory.Build.props", "XML")]
    [InlineData("App.xaml", "XML")]
    [InlineData("ForgeDesk.slnx", "XML")]
    [InlineData("styles.scss", "CSS")]
    [InlineData("build.ps1", "PowerShell")]
    [InlineData("README.md", "MarkDown")]
    [InlineData("main.cpp", "C++")]
    [InlineData("script.py", "Python")]
    [InlineData("changes.patch", "Patch")]
    [InlineData("query.sql", "TSQL")]
    public void Known_extensions_map_to_definitions(string path, string expected) =>
        SyntaxLanguageMap.DefinitionNameFor(path).Should().Be(expected);

    [Theory]
    [InlineData("Dockerfile")]
    [InlineData("notes.txt")]
    [InlineData("main.rs")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_files_are_plain_text(string? path) => SyntaxLanguageMap.DefinitionNameFor(path).Should().BeNull();
}
