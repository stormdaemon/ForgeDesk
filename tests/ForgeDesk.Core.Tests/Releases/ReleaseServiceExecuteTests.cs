using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Releases;
using ForgeDesk.Core.Tests.Infrastructure;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace ForgeDesk.Core.Tests.Releases;

public sealed class ReleaseServiceExecuteTests : IDisposable
{
    private const string HeadSha = "1234567890abcdef1234567890abcdef12345678";
    private const string OtherSha = "fedcba0987654321fedcba0987654321fedcba09";
    private const string DraftUrl = "https://github.com/octo/forge/releases/tag/untagged-1";
    private const string PublishedUrl = "https://github.com/octo/forge/releases/tag/v1.2.0";

    private static readonly GitHubRepoRef Repo = new("octo", "forge");

    private readonly TempDirectory _dir = new("release");
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly IGitHubService _github = Substitute.For<IGitHubService>();
    private readonly IActivityLog _activity = Substitute.For<IActivityLog>();
    private readonly AdjustableClock _clock = new();
    private readonly List<ReleaseStepUpdate> _updates = [];
    private readonly Project _project;
    private readonly ReleaseService _service;
    private readonly string _zip;
    private readonly string _exe;

    public ReleaseServiceExecuteTests()
    {
        _project = new Project { Id = "p1", Name = "forge", Path = _dir.Path, GitHub = Repo };
        _service = new ReleaseService(_git, _github, _activity, _clock);
        _zip = _dir.WriteFile("dist/forge-win-x64.zip", new string('z', 1000));
        _exe = _dir.WriteFile("dist/ForgeSetup.exe", new string('e', 500));

        _git.GetLogAsync(_dir.Path, Arg.Is<GitLogQuery>(q => q.Revision == "HEAD" && q.Take == 1), Arg.Any<CancellationToken>())
            .Returns([new GitCommit { Sha = HeadSha, Subject = "feat: x", Author = new GitSignature("a", "a@x", _clock.Now) }]);
        _git.GetTagsAsync(_dir.Path, Arg.Any<CancellationToken>()).Returns([]);
        _git.GetRemotesAsync(_dir.Path, Arg.Any<CancellationToken>()).Returns([
            new GitRemote("upstream", "https://github.com/someone-else/forge.git", null),
            new GitRemote("origin", "git@github.com:octo/forge.git", null),
        ]);
        _github.IsSignedIn.Returns(true);
        _github.GetTagsAsync(Repo, Arg.Any<CancellationToken>()).Returns([new GitHubTag("v1.1.0", OtherSha)]);
        _github.GetReleasesAsync(Repo, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _github.CreateReleaseAsync(Repo, Arg.Any<NewRelease>(), Arg.Any<CancellationToken>())
            .Returns(ci => Release(ci.Arg<NewRelease>().Draft));
        _github.UploadReleaseAssetAsync(Repo, 42, Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var progress = ci.Arg<IProgress<TransferProgress>?>();
                var size = new FileInfo(ci.Arg<string>()).Length;
                progress?.Report(new TransferProgress(size / 2, size));
                progress?.Report(new TransferProgress(size / 2, size));
                progress?.Report(new TransferProgress(size, size));
                return new GitHubReleaseAsset(7, Path.GetFileName(ci.Arg<string>()), size, 0, "https://example.com/a", "application/octet-stream");
            });
        _github.PublishReleaseAsync(Repo, 42, Arg.Any<CancellationToken>()).Returns(Release(draft: false));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Happy_path_with_assets_runs_every_step_and_publishes()
    {
        var result = await ExecuteAsync(Plan() with { Assets = [_zip, _exe] });

        result.Succeeded.Should().BeTrue();
        result.Error.Should().BeNull();
        result.Release!.HtmlUrl.Should().Be(PublishedUrl);
        States(result).Should().Equal(
            (ReleaseStepKind.Validate, ReleaseStepState.Succeeded),
            (ReleaseStepKind.CreateTag, ReleaseStepState.Succeeded),
            (ReleaseStepKind.PushTag, ReleaseStepState.Succeeded),
            (ReleaseStepKind.CreateRelease, ReleaseStepState.Succeeded),
            (ReleaseStepKind.UploadAsset, ReleaseStepState.Succeeded),
            (ReleaseStepKind.UploadAsset, ReleaseStepState.Succeeded),
            (ReleaseStepKind.Publish, ReleaseStepState.Succeeded));
        result.Steps.Where(s => s.Step == ReleaseStepKind.UploadAsset).Select(s => s.Title)
            .Should().Equal("Upload forge-win-x64.zip", "Upload ForgeSetup.exe");

        await _git.Received(1).CreateTagAsync(_dir.Path, "v1.2.0", "ForgeDesk 1.2", null, Arg.Any<CancellationToken>());
        await _git.Received(1).PushTagAsync(_dir.Path, "v1.2.0", "origin", Arg.Any<CancellationToken>());
        await _github.Received(1).CreateReleaseAsync(Repo, Arg.Is<NewRelease>(r =>
            r.TagName == "v1.2.0" && r.Name == "ForgeDesk 1.2" && r.Body == "Notes" && r.Draft && !r.Prerelease && r.MakeLatest && r.TargetCommitish == null),
            Arg.Any<CancellationToken>());
        await _github.Received(1).PublishReleaseAsync(Repo, 42, Arg.Any<CancellationToken>());
        await _activity.Received(1).RecordAsync(Arg.Is<ActivityEntry>(e =>
            e.Kind == ActivityKind.ReleasePublished && e.Outcome == ActivityOutcome.Success && e.ProjectId == "p1"
            && e.Title == "Published release v1.2.0" && e.RefKind == "url" && e.RefValue == PublishedUrl), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Progress_reports_pending_steps_first_then_every_transition_with_upload_fractions()
    {
        await ExecuteAsync(Plan() with { Assets = [_zip] });

        _updates.Take(6).Should().OnlyContain(u => u.State == ReleaseStepState.Pending);
        var upload = _updates.Where(u => u.Step == ReleaseStepKind.UploadAsset).ToList();
        upload.Select(u => (u.State, u.Progress)).Should().Equal(
            (ReleaseStepState.Pending, null),
            (ReleaseStepState.Running, null),
            (ReleaseStepState.Running, 0.5),
            (ReleaseStepState.Running, 1.0),
            (ReleaseStepState.Succeeded, 1.0));
        upload[2].Detail.Should().Be("500 B of 1000 B");
    }

    [Fact]
    public async Task Without_assets_the_release_is_created_public_directly()
    {
        var result = await ExecuteAsync(Plan());

        result.Succeeded.Should().BeTrue();
        await _github.Received(1).CreateReleaseAsync(Repo, Arg.Is<NewRelease>(r => !r.Draft), Arg.Any<CancellationToken>());
        await _github.DidNotReceiveWithAnyArgs().PublishReleaseAsync(default!, default, default);
        result.Steps[^1].Should().Match<ReleaseStepUpdate>(s => s.Step == ReleaseStepKind.Publish && s.State == ReleaseStepState.Succeeded);
    }

    [Fact]
    public async Task A_draft_plan_is_left_unpublished()
    {
        var result = await ExecuteAsync(Plan() with { Draft = true, Assets = [_zip] });

        result.Succeeded.Should().BeTrue();
        result.Release!.IsDraft.Should().BeTrue();
        result.Steps[^1].State.Should().Be(ReleaseStepState.Skipped);
        await _github.DidNotReceiveWithAnyArgs().PublishReleaseAsync(default!, default, default);
        await _activity.Received(1).RecordAsync(Arg.Is<ActivityEntry>(e => e.Title == "Created draft release v1.2.0" && e.RefValue == DraftUrl), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Prerelease_plans_are_not_marked_latest()
    {
        await ExecuteAsync(Plan() with { Prerelease = true });

        await _github.Received(1).CreateReleaseAsync(Repo, Arg.Is<NewRelease>(r => r.Prerelease && !r.MakeLatest), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_tag_creation_github_creates_the_tag_at_the_resolved_target()
    {
        var result = await ExecuteAsync(Plan() with { CreateAndPushTag = false });

        result.Succeeded.Should().BeTrue();
        States(result).Take(3).Should().Equal(
            (ReleaseStepKind.Validate, ReleaseStepState.Succeeded),
            (ReleaseStepKind.CreateTag, ReleaseStepState.Skipped),
            (ReleaseStepKind.PushTag, ReleaseStepState.Skipped));
        await _git.DidNotReceiveWithAnyArgs().CreateTagAsync(default!, default!, default, default, default);
        await _github.Received(1).CreateReleaseAsync(Repo, Arg.Is<NewRelease>(r => r.TargetCommitish == HeadSha), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_explicit_target_is_resolved_and_tagged()
    {
        _git.GetLogAsync(_dir.Path, Arg.Is<GitLogQuery>(q => q.Revision == "release/1.2"), Arg.Any<CancellationToken>())
            .Returns([new GitCommit { Sha = OtherSha, Subject = "x", Author = new GitSignature("a", "a@x", _clock.Now) }]);

        var result = await ExecuteAsync(Plan() with { Target = "release/1.2" });

        result.Succeeded.Should().BeTrue();
        await _git.Received(1).CreateTagAsync(_dir.Path, "v1.2.0", "ForgeDesk 1.2", "release/1.2", Arg.Any<CancellationToken>());
        result.Steps[0].Detail.Should().Be("v1.2.0 → fedcba0");
    }

    [Fact]
    public async Task A_local_tag_already_at_the_target_is_reused()
    {
        _git.GetTagsAsync(_dir.Path, Arg.Any<CancellationToken>()).Returns([new GitTag("v1.2.0", HeadSha, _clock.Now, "ForgeDesk 1.2", true)]);

        var result = await ExecuteAsync(Plan());

        result.Succeeded.Should().BeTrue();
        result.Steps[1].State.Should().Be(ReleaseStepState.Skipped);
        await _git.DidNotReceiveWithAnyArgs().CreateTagAsync(default!, default!, default, default, default);
        await _git.Received(1).PushTagAsync(_dir.Path, "v1.2.0", "origin", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_local_tag_on_another_commit_fails_the_tag_step()
    {
        _git.GetTagsAsync(_dir.Path, Arg.Any<CancellationToken>()).Returns([new GitTag("v1.2.0", OtherSha, _clock.Now, null, true)]);

        var result = await ExecuteAsync(Plan());

        AssertFailedAt(result, ReleaseStepKind.CreateTag, "A local tag v1.2.0 already exists and points to another commit.");
        await _git.DidNotReceiveWithAnyArgs().PushTagAsync(default!, default!, default, default);
    }

    [Theory]
    [InlineData("1.2", "1.2", "is not a valid version tag")]
    [InlineData("v1.2.0", "1.3.0", "does not match version 1.3.0")]
    [InlineData("v1.2.0", "latest", "is not a valid version")]
    public async Task Invalid_plans_fail_validation(string tag, string version, string expected)
    {
        var result = await ExecuteAsync(Plan() with { TagName = tag, Version = version });

        AssertFailedAt(result, ReleaseStepKind.Validate, expected);
        await _github.DidNotReceiveWithAnyArgs().CreateReleaseAsync(default!, default!, default);
    }

    [Fact]
    public async Task A_blank_title_fails_validation()
    {
        var result = await ExecuteAsync(Plan() with { Title = "  " });

        AssertFailedAt(result, ReleaseStepKind.Validate, "Give the release a title.");
    }

    [Fact]
    public async Task A_tag_already_on_github_at_another_commit_fails_validation()
    {
        _github.GetTagsAsync(Repo, Arg.Any<CancellationToken>()).Returns([new GitHubTag("v1.2.0", OtherSha)]);

        var result = await ExecuteAsync(Plan());

        AssertFailedAt(result, ReleaseStepKind.Validate, "The tag v1.2.0 already exists on GitHub and points to another commit.");
        result.Error.Should().EndWith("Choose a new version number, or delete the tag on GitHub first.", "the recovery hint is part of the message");
        await _git.DidNotReceiveWithAnyArgs().CreateTagAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task Plan_fields_are_trimmed_before_reaching_git_and_github()
    {
        var result = await ExecuteAsync(Plan() with { TagName = " v1.2.0 ", Version = "1.2.0 ", Title = "  ForgeDesk 1.2 ", Target = "  " });

        result.Succeeded.Should().BeTrue();
        result.Steps[1].Title.Should().Be("Create tag v1.2.0");
        await _git.Received(1).CreateTagAsync(_dir.Path, "v1.2.0", "ForgeDesk 1.2", null, Arg.Any<CancellationToken>());
        await _github.Received(1).CreateReleaseAsync(Repo, Arg.Is<NewRelease>(r => r.TagName == "v1.2.0" && r.Name == "ForgeDesk 1.2"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_already_published_release_fails_validation()
    {
        _github.GetReleasesAsync(Repo, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([Release(draft: false)]);

        var result = await ExecuteAsync(Plan());

        AssertFailedAt(result, ReleaseStepKind.Validate, "Release v1.2.0 is already published on GitHub.");
    }

    [Fact]
    public async Task Missing_or_duplicate_assets_fail_validation()
    {
        var missing = await ExecuteAsync(Plan() with { Assets = [_zip, Path.Combine(_dir.Path, "dist", "gone.zip")] });
        AssertFailedAt(missing, ReleaseStepKind.Validate, "The asset gone.zip was not found.");

        var copy = _dir.WriteFile("other/forge-win-x64.zip", "x");
        var duplicate = await ExecuteAsync(Plan() with { Assets = [_zip, copy] });
        AssertFailedAt(duplicate, ReleaseStepKind.Validate, "Two assets are named forge-win-x64.zip.");
    }

    [Fact]
    public async Task Not_being_signed_in_fails_validation()
    {
        _github.IsSignedIn.Returns(false);

        var result = await ExecuteAsync(Plan());

        AssertFailedAt(result, ReleaseStepKind.Validate, "You're not signed in to GitHub.");
    }

    [Fact]
    public async Task A_project_without_github_remote_fails_validation()
    {
        _git.GetRemotesAsync(_dir.Path, Arg.Any<CancellationToken>()).Returns([new GitRemote("origin", "https://gitlab.com/octo/forge.git", null)]);

        var result = await _service.ExecuteAsync(_project with { GitHub = null }, Plan(), Collector(), Ct);

        AssertFailedAt(result, ReleaseStepKind.Validate, "This project has no GitHub remote.");
    }

    [Fact]
    public async Task The_github_remote_is_found_from_the_git_remotes_when_the_project_has_none()
    {
        var result = await _service.ExecuteAsync(_project with { GitHub = null }, Plan(), Collector(), Ct);

        result.Succeeded.Should().BeTrue();
        await _github.Received().CreateReleaseAsync(Repo, Arg.Any<NewRelease>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_rejected_tag_push_stops_before_creating_the_release()
    {
        _git.PushTagAsync(_dir.Path, "v1.2.0", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ForgeException(ErrorKind.RemoteRejected, "GitHub rejected the push of v1.2.0."));

        var result = await ExecuteAsync(Plan() with { Assets = [_zip] });

        AssertFailedAt(result, ReleaseStepKind.PushTag, "GitHub rejected the push of v1.2.0.");
        result.Error.Should().Contain("The tag v1.2.0 was created locally and will be reused when you retry.");
        result.Steps.Skip(3).Should().OnlyContain(s => s.State == ReleaseStepState.Pending);
        await _github.DidNotReceiveWithAnyArgs().CreateReleaseAsync(default!, default!, default);
        await _activity.Received(1).RecordAsync(Arg.Is<ActivityEntry>(e =>
            e.Kind == ActivityKind.ReleaseFailed && e.Outcome == ActivityOutcome.Failure && e.Title == "Release v1.2.0 failed" && e.RefValue == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_release_creation_is_reported()
    {
        _github.CreateReleaseAsync(Repo, Arg.Any<NewRelease>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ForgeException(ErrorKind.NetworkUnavailable, "GitHub could not be reached."));

        var result = await ExecuteAsync(Plan());

        AssertFailedAt(result, ReleaseStepKind.CreateRelease, "GitHub could not be reached.");
        result.Release.Should().BeNull();
    }

    [Fact]
    public async Task A_failed_upload_keeps_the_draft_and_says_so()
    {
        _github.UploadReleaseAssetAsync(Repo, 42, _exe, Arg.Any<IProgress<TransferProgress>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ForgeException(ErrorKind.NetworkUnavailable, "The upload was interrupted."));

        var result = await ExecuteAsync(Plan() with { Assets = [_zip, _exe] });

        AssertFailedAt(result, ReleaseStepKind.UploadAsset, "The upload was interrupted.");
        result.Steps.Count(s => s.Step == ReleaseStepKind.UploadAsset && s.State == ReleaseStepState.Succeeded).Should().Be(1);
        result.Release!.IsDraft.Should().BeTrue();
        result.Error.Should().Contain("The draft release v1.2.0 was kept on GitHub");
        result.Steps[^1].State.Should().Be(ReleaseStepState.Pending);
        await _github.DidNotReceiveWithAnyArgs().PublishReleaseAsync(default!, default, default);
        await _github.DidNotReceiveWithAnyArgs().DeleteReleaseAsync(default!, default, default);
        await _activity.Received(1).RecordAsync(Arg.Is<ActivityEntry>(e =>
            e.Kind == ActivityKind.ReleaseFailed && e.RefKind == "url" && e.RefValue == DraftUrl), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_publish_keeps_the_complete_draft()
    {
        _github.PublishReleaseAsync(Repo, 42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new ForgeException(ErrorKind.RateLimited, "GitHub's rate limit was reached."));

        var result = await ExecuteAsync(Plan() with { Assets = [_zip] });

        AssertFailedAt(result, ReleaseStepKind.Publish, "GitHub's rate limit was reached.");
        result.Error.Should().Contain("was kept on GitHub");
    }

    [Fact]
    public async Task Unexpected_exceptions_fail_the_step_instead_of_escaping()
    {
        _github.CreateReleaseAsync(Repo, Arg.Any<NewRelease>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection reset by peer"));

        var result = await ExecuteAsync(Plan());

        AssertFailedAt(result, ReleaseStepKind.CreateRelease, "Connection reset by peer");
    }

    [Fact]
    public async Task Retrying_resumes_the_draft_left_by_a_failed_attempt()
    {
        var draft = Release(draft: true) with { Assets = [new GitHubReleaseAsset(1, "forge-win-x64.zip", 1000, 0, "https://example.com/z", "application/zip")] };
        _github.GetReleasesAsync(Repo, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([draft]);
        _github.GetTagsAsync(Repo, Arg.Any<CancellationToken>()).Returns([new GitHubTag("v1.2.0", HeadSha)]);
        _git.GetTagsAsync(_dir.Path, Arg.Any<CancellationToken>()).Returns([new GitTag("v1.2.0", HeadSha, _clock.Now, "ForgeDesk 1.2", true)]);

        var result = await ExecuteAsync(Plan() with { Assets = [_zip, _exe] });

        result.Succeeded.Should().BeTrue();
        States(result).Should().Equal(
            (ReleaseStepKind.Validate, ReleaseStepState.Succeeded),
            (ReleaseStepKind.CreateTag, ReleaseStepState.Skipped),
            (ReleaseStepKind.PushTag, ReleaseStepState.Skipped),
            (ReleaseStepKind.CreateRelease, ReleaseStepState.Skipped),
            (ReleaseStepKind.UploadAsset, ReleaseStepState.Skipped),
            (ReleaseStepKind.UploadAsset, ReleaseStepState.Succeeded),
            (ReleaseStepKind.Publish, ReleaseStepState.Succeeded));
        result.Steps[0].Detail.Should().Contain("Resuming");
        await _github.DidNotReceiveWithAnyArgs().CreateReleaseAsync(default!, default!, default);
        await _github.Received(1).UploadReleaseAssetAsync(Repo, 42, _exe, Arg.Any<IProgress<TransferProgress>?>(), Arg.Any<CancellationToken>());
        await _github.DidNotReceive().UploadReleaseAssetAsync(Repo, 42, _zip, Arg.Any<IProgress<TransferProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_different_file_with_the_same_name_on_the_draft_fails_the_upload()
    {
        var draft = Release(draft: true) with { Assets = [new GitHubReleaseAsset(1, "forge-win-x64.zip", 5, 0, "https://example.com/z", "application/zip")] };
        _github.GetReleasesAsync(Repo, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([draft]);

        var result = await ExecuteAsync(Plan() with { Assets = [_zip] });

        AssertFailedAt(result, ReleaseStepKind.UploadAsset, "The draft release already has a different file named forge-win-x64.zip.");
    }

    [Fact]
    public async Task Cancellation_during_an_upload_fails_the_step_as_cancelled()
    {
        using var cts = new CancellationTokenSource();
        _github.UploadReleaseAssetAsync(Repo, 42, _zip, Arg.Any<IProgress<TransferProgress>?>(), Arg.Any<CancellationToken>())
            .Returns<GitHubReleaseAsset>(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        var result = await _service.ExecuteAsync(_project, Plan() with { Assets = [_zip, _exe] }, Collector(), cts.Token);

        result.Succeeded.Should().BeFalse();
        var failed = result.Steps.Single(s => s.State == ReleaseStepState.Failed);
        failed.Step.Should().Be(ReleaseStepKind.UploadAsset);
        failed.Detail.Should().Be("Cancelled");
        result.Error.Should().StartWith("The release was cancelled.").And.Contain("was kept on GitHub");
        await _github.DidNotReceive().UploadReleaseAssetAsync(Repo, 42, _exe, Arg.Any<IProgress<TransferProgress>?>(), Arg.Any<CancellationToken>());
        await _activity.Received(1).RecordAsync(Arg.Is<ActivityEntry>(e =>
            e.Kind == ActivityKind.ReleaseFailed && e.Outcome == ActivityOutcome.Warning && e.Title == "Release v1.2.0 cancelled"), CancellationToken.None);
    }

    [Fact]
    public async Task An_already_cancelled_token_fails_the_first_step()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await _service.ExecuteAsync(_project, Plan(), Collector(), cts.Token);

        result.Steps[0].Should().Match<ReleaseStepUpdate>(s => s.State == ReleaseStepState.Failed && s.Detail == "Cancelled");
        await _github.DidNotReceiveWithAnyArgs().GetReleasesAsync(default!, default, default);
    }

    [Fact]
    public async Task A_faulty_progress_handler_does_not_break_the_release()
    {
        var progress = Substitute.For<IProgress<ReleaseStepUpdate>>();
        progress.When(p => p.Report(Arg.Any<ReleaseStepUpdate>())).Do(_ => throw new InvalidOperationException("UI bug"));

        var result = await _service.ExecuteAsync(_project, Plan() with { Assets = [_zip] }, progress, Ct);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Null_arguments_are_programming_errors()
    {
        var noProject = () => _service.ExecuteAsync(null!, Plan(), cancellationToken: Ct);
        var noPlan = () => _service.ExecuteAsync(_project, null!, cancellationToken: Ct);

        await noProject.Should().ThrowAsync<ArgumentNullException>();
        await noPlan.Should().ThrowAsync<ArgumentNullException>();
    }

    private static ReleasePlan Plan() => new()
    {
        Version = "1.2.0",
        TagName = "v1.2.0",
        Title = "ForgeDesk 1.2",
        Notes = "Notes",
    };

    private static GitHubRelease Release(bool draft) => new()
    {
        Id = 42,
        TagName = "v1.2.0",
        Name = "ForgeDesk 1.2",
        IsDraft = draft,
        HtmlUrl = draft ? DraftUrl : PublishedUrl,
    };

    private static List<(ReleaseStepKind, ReleaseStepState)> States(ReleaseResult result) =>
        result.Steps.Select(s => (s.Step, s.State)).ToList();

    private static void AssertFailedAt(ReleaseResult result, ReleaseStepKind step, string message)
    {
        result.Succeeded.Should().BeFalse();
        var failed = result.Steps.Should().ContainSingle(s => s.State == ReleaseStepState.Failed).Which;
        failed.Step.Should().Be(step);
        failed.Detail.Should().Contain(message);
        result.Error.Should().Contain(message);
        result.Steps.SkipWhile(s => s.State != ReleaseStepState.Failed).Skip(1).Should().OnlyContain(s => s.State == ReleaseStepState.Pending);
    }

    private Task<ReleaseResult> ExecuteAsync(ReleasePlan plan) => _service.ExecuteAsync(_project, plan, Collector(), Ct);

    private SynchronousProgress Collector() => new(_updates);

    private sealed class SynchronousProgress(List<ReleaseStepUpdate> sink) : IProgress<ReleaseStepUpdate>
    {
        public void Report(ReleaseStepUpdate value)
        {
            lock (sink)
            {
                sink.Add(value);
            }
        }
    }
}
