using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Releases;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Releases;
using ForgeDesk.Presentation.Tests.GitHub.Support;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Releases;

public sealed class ReleaseWizardViewModelTests : IDisposable
{
    private readonly GitHubHarness _harness = new();
    private readonly TestFolder _files = new();
    private readonly List<ReleaseWizardViewModel> _wizards = [];
    private ReleaseContext _context;
    private int _closed;

    public ReleaseWizardViewModelTests()
    {
        _context = new ReleaseContext
        {
            LatestTag = "v1.1.0",
            LatestVersion = "1.1.0",
            TagPrefix = "v",
            Suggestions =
            [
                new VersionSuggestion("Minor", "1.2.0", "Recommended: 1 new feature since v1.1.0."),
                new VersionSuggestion("Patch", "1.1.1", "Only fixes."),
                new VersionSuggestion("Major", "2.0.0", "Breaking changes."),
            ],
            CommitsSinceLatest = [Commit("a1", "feat: add login"), Commit("b2", "fix: crash on start")],
            DraftNotes = "Prepared draft",
            CurrentBranch = "main",
            DefaultBranch = "main",
            HeadSha = "1234567890abcdef",
            Warnings = ["2 commits on main are not pushed."],
            SuggestedAssets = [File("dist/forge-1.2.0.zip", 2048)],
        };
        _harness.Releases.PrepareAsync(Arg.Any<Project>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(_context));
    }

    public void Dispose()
    {
        foreach (var wizard in _wizards)
        {
            wizard.Dispose();
        }

        _harness.Dispose();
        _files.Dispose();
    }

    [Fact]
    public async Task Prepare_fills_the_form_with_the_recommended_version()
    {
        var wizard = await OpenAsync();

        wizard.IsPrepared.Should().BeTrue();
        wizard.VersionOptions.Select(o => o.TagName).Should().Equal("v1.2.0", "v1.1.1", "v2.0.0");
        wizard.VersionOptions[0].IsRecommended.Should().BeTrue();
        wizard.VersionOptions[0].IsSelected.Should().BeTrue();
        wizard.TagName.Should().Be("v1.2.0");
        wizard.ReleaseTitle.Should().Be("forge-app 1.2.0");
        wizard.Notes.Should().Be("Prepared draft");
        wizard.NotesEdited.Should().BeFalse();
        wizard.LatestText.Should().Be("Latest release v1.1.0 · 2 commits since");
        wizard.TargetOptions.Select(t => t.Value).Should().Equal("main", "1234567890abcdef");
        wizard.SelectedTarget!.Value.Should().Be("main");
        wizard.HasWarnings.Should().BeTrue();
        wizard.Assets.Should().ContainSingle(a => a.IsSuggested && !a.IsSelected && a.Size == 2048);
        wizard.NextCommand.CanExecute(null).Should().BeTrue("warnings don't block");
    }

    [Fact]
    public async Task Blockers_prevent_continuing()
    {
        _context = _context with { Blockers = ["You're not signed in to GitHub. Sign in to publish releases."] };

        var wizard = await OpenAsync();

        wizard.HasBlockers.Should().BeTrue();
        wizard.NextCommand.CanExecute(null).Should().BeFalse();
        await wizard.PublishCommand.ExecuteAsync(null);
        await _harness.Releases.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Custom_versions_are_validated_with_semver()
    {
        var wizard = await OpenAsync();

        wizard.ChooseCustomVersionCommand.Execute(null);
        wizard.UseCustomVersion.Should().BeTrue();
        wizard.CustomVersion.Should().Be("1.2.0", "the custom box starts from the selected suggestion");
        wizard.VersionOptions.Should().OnlyContain(o => !o.IsSelected);

        wizard.CustomVersion = "1.2";
        wizard.VersionError.Should().Contain("isn't a valid version");
        wizard.TagName.Should().BeNull();
        wizard.NextCommand.CanExecute(null).Should().BeFalse();

        wizard.CustomVersion = "v2.0.0-beta.1";
        wizard.VersionError.Should().BeNull();
        wizard.TagName.Should().Be("v2.0.0-beta.1");
        wizard.IsPrerelease.Should().BeTrue("a pre-release version is published as one");
        wizard.ReleaseTitle.Should().Be("forge-app 2.0.0-beta.1");

        wizard.CustomVersion = "1.0.5";
        wizard.VersionWarning.Should().Contain("isn't newer");
        wizard.NextCommand.CanExecute(null).Should().BeTrue();

        wizard.SelectOptionCommand.Execute(wizard.VersionOptions[2]);
        wizard.UseCustomVersion.Should().BeFalse();
        wizard.TagName.Should().Be("v2.0.0");
        wizard.IsPrerelease.Should().BeFalse();
    }

    [Fact]
    public async Task Edited_title_and_prerelease_stop_following_the_version()
    {
        var wizard = await OpenAsync();

        wizard.ReleaseTitle = "The big one";
        wizard.IsPrerelease = true;
        wizard.SelectOptionCommand.Execute(wizard.VersionOptions[2]);

        wizard.ReleaseTitle.Should().Be("The big one");
        wizard.IsPrerelease.Should().BeTrue();

        wizard.ReleaseTitle = " ";
        wizard.NextCommand.CanExecute(null).Should().BeFalse();
        wizard.ValidationError(ReleaseWizardStep.Version).Should().Be("Give the release a title.");
    }

    [Fact]
    public async Task Notes_are_rebuilt_for_a_new_version_until_edited()
    {
        var wizard = await OpenAsync();

        wizard.SelectOptionCommand.Execute(wizard.VersionOptions[1]);

        wizard.Notes.Should().Contain("Add login").And.Contain("Crash on start").And.Contain("/compare/v1.1.0...v1.1.1");
        wizard.NotesEdited.Should().BeFalse();

        wizard.Notes += "\nThanks to everyone!";
        wizard.NotesEdited.Should().BeTrue();
        var edited = wizard.Notes;

        wizard.SelectOptionCommand.Execute(wizard.VersionOptions[2]);
        wizard.Notes.Should().Be(edited);

        wizard.RebuildNotesCommand.Execute(null);
        wizard.NotesEdited.Should().BeFalse();
        wizard.Notes.Should().Contain("/compare/v1.1.0...v2.0.0").And.NotContain("Thanks");
    }

    [Fact]
    public async Task Generated_notes_replace_the_draft_after_confirmation()
    {
        _harness.GitHub.GenerateReleaseNotesAsync(GitHubHarness.Repo, "v1.2.0", "v1.1.0", "main", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("## What's Changed\n* Login by @ada"));
        var wizard = await OpenAsync();

        _harness.Dialogs.ConfirmAsync(Arg.Any<ConfirmOptions>()).Returns(false);
        await wizard.GenerateNotesCommand.ExecuteAsync(null);
        wizard.Notes.Should().Be("Prepared draft");
        await _harness.GitHub.DidNotReceiveWithAnyArgs().GenerateReleaseNotesAsync(default!, default!, default, default, default);

        _harness.Dialogs.ConfirmAsync(Arg.Any<ConfirmOptions>()).Returns(true);
        await wizard.GenerateNotesCommand.ExecuteAsync(null);

        wizard.Notes.Should().StartWith("## What's Changed");
        wizard.NotesEdited.Should().BeTrue("GitHub's notes must survive a version change");
    }

    [Fact]
    public async Task Assets_are_added_ticked_validated_and_summed()
    {
        var picked = File("out/forge-1.2.0.zip", 1024);
        var other = File("setup/ForgeSetup.exe", 4096);
        _harness.Dialogs.PickFilesAsync(Arg.Any<string>(), Arg.Any<string?>(), true).Returns(Task.FromResult<IReadOnlyList<string>>([picked, other]));
        var wizard = await OpenAsync();
        wizard.AssetsSummary.Should().Be("No files will be attached");

        await wizard.AddFilesCommand.ExecuteAsync(null);

        wizard.Assets.Should().HaveCount(3);
        wizard.SelectedAssets.Select(a => a.Name).Should().Equal("forge-1.2.0.zip", "ForgeSetup.exe");
        wizard.AssetsSummary.Should().Be($"2 files · {Format.Bytes(5120)}");
        wizard.AssetsError.Should().BeNull();

        wizard.Assets[0].IsSelected = true;
        wizard.AssetsError.Should().Contain("Two files are named forge-1.2.0.zip");
        wizard.ValidationError(ReleaseWizardStep.Assets).Should().NotBeNull();

        wizard.RemoveAssetCommand.Execute(wizard.Assets[1]);
        wizard.AssetsError.Should().BeNull();
        wizard.SelectedAssets.Select(a => a.Path).Should().Equal(_context.SuggestedAssets[0], other);
    }

    [Fact]
    public async Task Steps_go_forward_and_back_and_the_header_cannot_skip_invalid_steps()
    {
        var wizard = await OpenAsync();
        wizard.Steps.Select(s => s.Title).Should().Equal("Version", "Notes", "Assets", "Review");

        await wizard.NextCommand.ExecuteAsync(null);
        wizard.IsNotesStep.Should().BeTrue();
        wizard.Steps[0].IsCompleted.Should().BeTrue();
        wizard.Steps[1].IsCurrent.Should().BeTrue();
        await wizard.NextCommand.ExecuteAsync(null);
        wizard.IsAssetsStep.Should().BeTrue();
        await wizard.NextCommand.ExecuteAsync(null);
        wizard.IsReviewStep.Should().BeTrue();
        wizard.NextText.Should().Be("Publish release");
        wizard.ReviewKind.Should().Be("Published as the latest release");

        wizard.BackCommand.Execute(null);
        wizard.IsAssetsStep.Should().BeTrue();

        wizard.GoToStepCommand.Execute(wizard.Steps[0]);
        wizard.ChooseCustomVersionCommand.Execute(null);
        wizard.CustomVersion = "not a version";
        wizard.GoToStepCommand.Execute(wizard.Steps[3]);
        wizard.IsVersionStep.Should().BeTrue("the version must be fixed before reviewing");
    }

    [Fact]
    public async Task Execution_maps_each_step_update_to_a_timeline_row_and_retry_runs_the_same_plan()
    {
        var picked = File("out/a/forge.zip", 10);
        var plans = new List<ReleasePlan>();
        var attempt = 0;
        _harness.Releases.ExecuteAsync(Arg.Any<Project>(), Arg.Any<ReleasePlan>(), Arg.Any<IProgress<ReleaseStepUpdate>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                plans.Add(call.Arg<ReleasePlan>());
                var progress = call.Arg<IProgress<ReleaseStepUpdate>?>()!;
                var steps = new List<ReleaseStepUpdate>
                {
                    new(ReleaseStepKind.Validate, ReleaseStepState.Pending, "Check the release"),
                    new(ReleaseStepKind.CreateRelease, ReleaseStepState.Pending, "Create the release on GitHub"),
                    new(ReleaseStepKind.UploadAsset, ReleaseStepState.Pending, "Upload forge.zip"),
                    new(ReleaseStepKind.UploadAsset, ReleaseStepState.Pending, "Upload forge.zip"),
                    new(ReleaseStepKind.Publish, ReleaseStepState.Pending, "Publish the release"),
                };
                steps.ForEach(progress.Report);
                progress.Report(steps[0] with { State = ReleaseStepState.Succeeded });
                progress.Report(steps[1] with { State = ReleaseStepState.Succeeded, Detail = "Created as a draft" });
                progress.Report(steps[2] with { State = ReleaseStepState.Running, Detail = "5 B of 10 B", Progress = 0.5 });
                if (attempt++ == 0)
                {
                    progress.Report(steps[2] with { State = ReleaseStepState.Failed, Detail = "Connection reset" });
                    return Task.FromResult(new ReleaseResult(false, null, [], "Connection reset. The draft release v1.2.0 was kept on GitHub."));
                }

                progress.Report(steps[2] with { State = ReleaseStepState.Succeeded });
                progress.Report(steps[3] with { State = ReleaseStepState.Running, Progress = 0.25 });
                progress.Report(steps[3] with { State = ReleaseStepState.Succeeded });
                progress.Report(steps[4] with { State = ReleaseStepState.Succeeded });
                return Task.FromResult(new ReleaseResult(true, GitHubData.Release(10, "v1.2.0"), [], null));
            });
        _harness.Dialogs.PickFilesAsync(Arg.Any<string>(), Arg.Any<string?>(), true).Returns(Task.FromResult<IReadOnlyList<string>>([picked]));
        var wizard = await OpenAsync();
        await wizard.AddFilesCommand.ExecuteAsync(null);

        // The service reports two uploads with the same title: each must keep its own row.
        await wizard.PublishCommand.ExecuteAsync(null);

        wizard.IsPublishStep.Should().BeTrue();
        wizard.IsFailed.Should().BeTrue();
        wizard.FailureMessage.Should().Contain("was kept on GitHub");
        wizard.Timeline.Select(r => r.State).Should().Equal(ReleaseStepState.Succeeded, ReleaseStepState.Succeeded, ReleaseStepState.Failed,
            ReleaseStepState.Pending, ReleaseStepState.Pending);
        wizard.Timeline[1].Detail.Should().Be("Created as a draft");
        wizard.Timeline[2].Detail.Should().Be("Connection reset");
        plans[0].TagName.Should().Be("v1.2.0");
        plans[0].Assets.Should().Equal(picked);
        plans[0].Target.Should().Be("main");
        plans[0].Title.Should().Be("forge-app 1.2.0");
        plans[0].Notes.Should().Be("Prepared draft");

        await wizard.RetryCommand.ExecuteAsync(null);

        plans.Should().HaveCount(2);
        plans[1].Should().BeSameAs(plans[0]);
        wizard.IsSucceeded.Should().BeTrue();
        wizard.Timeline.Should().HaveCount(5);
        wizard.Timeline.Should().OnlyContain(r => r.IsSucceeded);
        wizard.Timeline[3].IsSucceeded.Should().BeTrue("the second upload of the same name is its own row");
        wizard.SuccessTitle.Should().Be("forge-app 1.2.0 is live");
        _harness.Notifications.Received(1).ShowSystemNotification("forge-app 1.2.0 is live", Arg.Any<string>(), _harness.Project.Id);

        wizard.ViewOnGitHubCommand.Execute(null);
        _harness.Shell.Received(1).OpenUrl("https://github.com/acme/forge-app/releases/tag/v1.2.0");
    }

    [Fact]
    public void A_running_upload_shows_its_progress()
    {
        var row = new ReleaseTimelineStepViewModel(new ReleaseStepUpdate(ReleaseStepKind.UploadAsset, ReleaseStepState.Running, "Upload a.zip", "1 KB of 4 KB", 0.25));

        row.HasProgress.Should().BeTrue();
        row.ProgressPercent.Should().Be(25);
        row.StateText.Should().Be("In progress");

        row.Apply(new ReleaseStepUpdate(ReleaseStepKind.UploadAsset, ReleaseStepState.Succeeded, "Upload a.zip"));
        row.HasProgress.Should().BeFalse();
        row.StateIcon.Should().Be("CheckmarkCircle16");
    }

    [Fact]
    public async Task Stopping_a_running_release_asks_then_cancels_it()
    {
        var running = new TaskCompletionSource<ReleaseResult>();
        var token = CancellationToken.None;
        _harness.Releases.ExecuteAsync(Arg.Any<Project>(), Arg.Any<ReleasePlan>(), Arg.Any<IProgress<ReleaseStepUpdate>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                token = call.Arg<CancellationToken>();
                token.Register(() => running.TrySetResult(new ReleaseResult(false, null, [], "The release was cancelled.")));
                return running.Task;
            });
        var wizard = await OpenAsync();

        var publishing = wizard.PublishCommand.ExecuteAsync(null);
        wizard.IsExecuting.Should().BeTrue();
        wizard.CancelText.Should().Be("Stop");

        wizard.CloseCommand.Execute(null);
        _closed.Should().Be(0, "the wizard can't be closed while publishing");

        _harness.Dialogs.ConfirmAsync(Arg.Any<ConfirmOptions>()).Returns(false);
        await wizard.CancelCommand.ExecuteAsync(null);
        token.IsCancellationRequested.Should().BeFalse();

        _harness.Dialogs.ConfirmAsync(Arg.Any<ConfirmOptions>()).Returns(true);
        await wizard.CancelCommand.ExecuteAsync(null);
        await publishing;

        await _harness.Dialogs.Received().ConfirmAsync(Arg.Is<ConfirmOptions>(o => o.IsDestructive && o.ConfirmText == "Stop publishing"));
        token.IsCancellationRequested.Should().BeTrue();
        wizard.IsExecuting.Should().BeFalse();
        wizard.IsFailed.Should().BeTrue();

        await wizard.CancelCommand.ExecuteAsync(null);
        _closed.Should().Be(1);
    }

    private async Task<ReleaseWizardViewModel> OpenAsync(string? version = null)
    {
        await _harness.Context.RefreshGitStatusAsync();
        var wizard = new ReleaseWizardViewModel(_harness.Context, _harness.Services, _harness.Releases, _harness.GitHub, ["v1.1.0", "v1.0.0"], version, _ => _closed++);
        _wizards.Add(wizard);
        await wizard.InitializeAsync();
        return wizard;
    }

    private string File(string relative, int size)
    {
        var path = _files.Combine(relative.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    private static GitCommit Commit(string sha, string subject) => new()
    {
        Sha = sha.PadRight(40, '0'),
        Subject = subject,
        Author = new GitSignature("Ada", "ada@example.com", TestData.Now),
    };
}
