using ForgeDesk.Core.Common;
using ForgeDesk.Core.Settings;
using ForgeDesk.Core.Terminal;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Settings;
using ForgeDesk.Presentation.Tests.Settings.Support;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Settings;

public sealed class SettingsViewModelTests : IDisposable
{
    private readonly SettingsHarness _harness = new();
    private SettingsViewModel? _page;

    public void Dispose()
    {
        _page?.Dispose();
        _harness.Dispose();
    }

    [Fact]
    public void The_sections_are_listed_in_order_starting_with_general()
    {
        var page = Create();

        page.Sections.Select(s => s.Title).Should().Equal("General", "Appearance", "Git", "GitHub", "Terminal", "Commands", "Editor", "Updates",
            "Data & storage", "About");
        page.SelectedSection.Should().BeSameAs(page.General);
        page.Sections.Select(s => s.AutomationId).Should().OnlyHaveUniqueItems().And.Contain("Settings.Section.GitHub");
    }

    [Theory]
    [InlineData("GitHub", "GitHub")]
    [InlineData("github", "GitHub")]
    [InlineData("Data", "Data & storage")]
    [InlineData("Data & storage", "Data & storage")]
    [InlineData("updates", "Updates")]
    public async Task The_navigation_argument_selects_a_section(string argument, string expectedTitle)
    {
        var page = Create();

        await page.OnNavigatedToAsync(argument);

        page.SelectedSection.Title.Should().Be(expectedTitle);
    }

    [Fact]
    public void Deselecting_the_section_keeps_the_one_shown()
    {
        var page = Create();
        page.SelectedSection = page.About;

        page.SelectedSection = null!;

        page.SelectedSection.Should().BeSameAs(page.About);
    }

    [Fact]
    public async Task An_unknown_section_keeps_the_current_one()
    {
        var page = Create();
        page.SelectedSection = page.Git;

        await page.OnNavigatedToAsync("nowhere");

        page.SelectedSection.Should().BeSameAs(page.Git);
    }

    [Fact]
    public void Settings_changed_elsewhere_show_up_without_being_saved_back()
    {
        var page = Create();
        var saves = _harness.Settings.SaveCount;

        _harness.Settings.ChangeExternally(s => s with { Theme = ThemePreference.Light, AutoFetch = false, RunHistoryPerProject = 50 });

        page.Appearance.SelectedTheme.Value.Should().Be(ThemePreference.Light);
        page.Git.AutoFetch.Should().BeFalse();
        page.Commands.HistoryPerProject.Should().Be(50);
        _harness.Settings.SaveCount.Should().Be(saves);
    }

    [Fact]
    public async Task Selecting_a_section_loads_it_once()
    {
        _harness.Shells.DiscoverAsync(Arg.Any<CancellationToken>()).Returns([new ShellProfile("pwsh", "PowerShell 7", ShellKind.PowerShellCore, "pwsh.exe", "")]);
        var page = Create();

        page.SelectedSection = page.Terminal;
        await page.OnNavigatedToAsync(null);

        await _harness.Shells.Received(1).DiscoverAsync(Arg.Any<CancellationToken>());
        page.Terminal.Shells.Select(s => s.Name).Should().Equal("Automatic", "PowerShell 7");
    }

    [Fact]
    public async Task Refresh_reloads_the_section_on_screen()
    {
        _harness.Git.FindGitAsync(Arg.Any<CancellationToken>()).Returns(new Core.Git.GitInstallation("/usr/bin/git", "2.46.0"));
        _harness.Git.GetIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new Core.Git.GitIdentity("Ada", "ada@example.com"));
        var page = Create();
        page.SelectedSection = page.Git;
        await page.OnNavigatedToAsync(null);

        await page.RefreshCommand.ExecuteAsync(null);

        await _harness.Git.Received(2).FindGitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_section_that_fails_to_load_shows_its_error()
    {
        _harness.Shells.DiscoverAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<ShellProfile>>(new ForgeException(ErrorKind.PermissionDenied, "Access denied.")));
        var page = Create();

        await page.OnNavigatedToAsync("Terminal");

        page.Terminal.Error!.Title.Should().Be("Could not list the installed shells");
    }

    private SettingsViewModel Create() => _page = _harness.CreatePage();
}
