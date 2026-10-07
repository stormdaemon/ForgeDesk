using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Releases;
using ForgeDesk.Presentation.Releases;
using ForgeDesk.Presentation.Tests.GitHub.Support;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Releases;

public sealed class ReleasesViewModelTests : IDisposable
{
    private GitHubHarness _harness = new();

    public ReleasesViewModelTests()
    {
        Releases(GitHubData.Release(3, "v1.2.0-rc.1", prerelease: true), GitHubData.Release(4, "v1.1.0"), GitHubData.Release(9, "v1.0.0"));
        _harness.Releases.PrepareAsync(Arg.Any<Project>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new ReleaseContext
        {
            LatestTag = "v1.1.0",
            LatestVersion = "1.1.0",
            Suggestions = [new VersionSuggestion("Minor", "1.2.0", "New features")],
        }));
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task Releases_are_listed_with_latest_assets_and_the_newest_notes_open()
    {
        var releases = await _harness.OpenReleasesAsync();

        releases.Releases.Select(r => r.TagName).Should().Equal("v1.2.0-rc.1", "v1.1.0", "v1.0.0");
        releases.Releases.Single(r => r.IsLatest).TagName.Should().Be("v1.1.0", "a pre-release is never the latest");
        releases.Releases[0].IsPrerelease.Should().BeTrue();
        releases.Releases[0].IsExpanded.Should().BeTrue();
        releases.Releases[1].IsExpanded.Should().BeFalse();
        releases.Releases[1].Assets.Should().ContainSingle(a => a.Name == "forge.zip" && a.SizeText == Presentation.Infrastructure.Format.Bytes(1024 * 1024) && a.DownloadsText == "12 downloads");
        releases.IsEmpty.Should().BeFalse();

        releases.OpenAssetCommand.Execute(releases.Releases[1].Assets[0]);
        _harness.Shell.Received(1).OpenUrl("https://github.com/acme/forge-app/releases/download/v1.1.0/forge.zip");
        releases.ToggleNotesCommand.Execute(releases.Releases[1]);
        releases.Releases[1].IsExpanded.Should().BeTrue();
    }

    [Fact]
    public async Task No_release_offers_the_first_one()
    {
        Releases();

        var releases = await _harness.OpenReleasesAsync();

        releases.IsEmpty.Should().BeTrue();
        await releases.NewReleaseCommand.ExecuteAsync(null);
        releases.IsWizardOpen.Should().BeTrue();
        releases.IsListVisible.Should().BeFalse();
    }

    [Fact]
    public async Task Local_version_tags_without_a_release_are_hinted_newest_first()
    {
        _harness.Tags =
        [
            new GitTag("v1.0.0", "a", null, null, false),
            new GitTag("v1.3.0", "b", null, null, true),
            new GitTag("nightly", "c", null, null, false),
            new GitTag("v1.2.1", "d", null, null, true),
        ];

        var releases = await _harness.OpenReleasesAsync();

        releases.UnreleasedTags.Should().Equal("v1.3.0", "v1.2.1");
        releases.UnreleasedTagsText.Should().Be("v1.3.0, v1.2.1 are tagged locally but have no GitHub release.");

        await releases.ReleaseTagCommand.ExecuteAsync("v1.3.0");

        releases.Wizard!.UseCustomVersion.Should().BeTrue();
        releases.Wizard.TagName.Should().Be("v1.3.0");
    }

    [Fact]
    public async Task Load_failure_is_inline_with_retry()
    {
        _harness.GitHub.GetReleasesAsync(GitHubHarness.Repo, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<GitHubRelease>>(new ForgeException(ErrorKind.NetworkUnavailable, "GitHub is unreachable.")));

        var releases = await _harness.OpenReleasesAsync();

        releases.Error!.Kind.Should().Be(ErrorKind.NetworkUnavailable);
        releases.IsEmpty.Should().BeFalse("an error is not an empty list");

        Releases(GitHubData.Release(4, "v1.1.0"));
        await releases.ReloadCommand.ExecuteAsync(null);
        releases.Error.Should().BeNull();
        releases.Releases.Should().ContainSingle();
    }

    [Fact]
    public async Task Refresh_bypasses_the_cache()
    {
        var releases = await _harness.OpenReleasesAsync();
        _harness.GitHub.ClearReceivedCalls();

        await releases.RefreshCommand.ExecuteAsync(null);

        Received.InOrder(() =>
        {
            _harness.GitHub.InvalidateCache(GitHubHarness.Repo);
            _harness.GitHub.GetReleasesAsync(GitHubHarness.Repo, ReleasesViewModel.MaxReleases, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Preconditions_match_the_github_tab()
    {
        _harness.Dispose();
        _harness = new GitHubHarness(linked: false);
        var releases = await _harness.OpenReleasesAsync();
        releases.IsNotLinked.Should().BeTrue();
        releases.IsListVisible.Should().BeFalse();
        await _harness.GitHub.DidNotReceiveWithAnyArgs().GetReleasesAsync(default!, default, default);

        await releases.RedetectCommand.ExecuteAsync(null);

        releases.IsReady.Should().BeTrue();
        releases.IsListVisible.Should().BeTrue();
        await _harness.GitHub.Received(1).GetReleasesAsync(GitHubHarness.Repo, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Palette_link_opens_the_wizard_once_the_tab_is_visible()
    {
        var releases = await _harness.OpenReleasesAsync(activate: false);

        await releases.NavigateToAsync(ReleasesNavigation.NewRelease());
        releases.IsWizardOpen.Should().BeFalse();

        await releases.ActivateAsync();

        releases.IsWizardOpen.Should().BeTrue();
        releases.Wizard!.IsPrepared.Should().BeTrue();
        releases.Wizard.TagName.Should().Be("v1.2.0");
    }

    [Fact]
    public async Task Closing_the_wizard_returns_to_the_list_and_publishing_reloads_it()
    {
        var plan = default(ReleasePlan);
        _harness.Releases.ExecuteAsync(Arg.Any<Project>(), Arg.Do<ReleasePlan>(p => plan = p), Arg.Any<IProgress<ReleaseStepUpdate>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ReleaseResult(true, GitHubData.Release(10, "v1.2.0"), [], null)));
        var releases = await _harness.OpenReleasesAsync();
        await releases.NewReleaseCommand.ExecuteAsync(null);
        var wizard = releases.Wizard!;
        _harness.GitHub.ClearReceivedCalls();

        await wizard.PublishCommand.ExecuteAsync(null);

        plan!.TagName.Should().Be("v1.2.0");
        _harness.GitHub.Received(1).InvalidateCache(GitHubHarness.Repo);
        await _harness.GitHub.Received(1).GetReleasesAsync(GitHubHarness.Repo, Arg.Any<int>(), Arg.Any<CancellationToken>());

        wizard.CloseCommand.Execute(null);
        releases.IsWizardOpen.Should().BeFalse();
        releases.IsListVisible.Should().BeTrue();
    }

    [Fact]
    public async Task Existing_release_tags_cannot_be_reused()
    {
        var releases = await _harness.OpenReleasesAsync();
        await releases.NewReleaseCommand.ExecuteAsync(null);
        var wizard = releases.Wizard!;

        wizard.ChooseCustomVersionCommand.Execute(null);
        wizard.CustomVersion = "1.1.0";

        wizard.VersionError.Should().Contain("already exists");
        wizard.NextCommand.CanExecute(null).Should().BeFalse();
    }

    private void Releases(params GitHubRelease[] releases) =>
        _harness.GitHub.GetReleasesAsync(GitHubHarness.Repo, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<GitHubRelease>>(releases));
}
