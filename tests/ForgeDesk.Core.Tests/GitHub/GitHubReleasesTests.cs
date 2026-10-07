using System.Net;
using System.Text.Json;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Tests.GitHub.Fakes;
using ForgeDesk.Core.Tests.Infrastructure;
using static ForgeDesk.Core.Tests.GitHub.Fakes.GitHubPayloads;

namespace ForgeDesk.Core.Tests.GitHub;

public class GitHubReleasesTests
{
    private const string ReleasesPath = "/repos/octo/app/releases";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Releases_are_listed_with_their_assets()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnGet(ReleasesPath, Array([
            Release(2, "v1.1.0-beta.1", prerelease: true),
            Release(1, "v1.0.0", assets: Array([Asset(10, "ForgeDesk-Setup.exe", 52_428_800, "application/vnd.microsoft.portable-executable")])),
        ]));

        var releases = await h.Service.GetReleasesAsync(GitHubServiceHarness.Repo, 10, Ct);

        releases.Should().HaveCount(2);
        releases[0].IsPrerelease.Should().BeTrue();
        var stable = releases[1];
        stable.TagName.Should().Be("v1.0.0");
        stable.Name.Should().Be("Release v1.0.0");
        stable.Body.Should().Contain("What's changed");
        stable.IsDraft.Should().BeFalse();
        stable.Author.Should().Be("octocat");
        stable.TargetCommitish.Should().Be("main");
        stable.PublishedAt.Should().Be(new DateTimeOffset(2026, 9, 1, 11, 0, 0, TimeSpan.Zero));
        stable.Assets.Should().Equal(new GitHubReleaseAsset(10, "ForgeDesk-Setup.exe", 52_428_800, 7,
            "https://github.com/octo/app/releases/download/v1.0.0/ForgeDesk-Setup.exe", "application/vnd.microsoft.portable-executable"));
    }

    [Fact]
    public async Task Creating_a_release_sends_draft_prerelease_and_latest_flags()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnPost(ReleasesPath, Release(5, "v2.0.0", draft: true));

        var release = await h.Service.CreateReleaseAsync(GitHubServiceHarness.Repo, new NewRelease
        {
            TagName = " v2.0.0 ",
            Name = "ForgeDesk 2.0",
            Body = "Notes",
            TargetCommitish = "release/2.0",
            Draft = true,
            MakeLatest = true,
        }, Ct);

        release.IsDraft.Should().BeTrue();
        using var body = JsonDocument.Parse(h.Api.Requests.Single().Body);
        var root = body.RootElement;
        root.GetProperty("tag_name").GetString().Should().Be("v2.0.0");
        root.GetProperty("name").GetString().Should().Be("ForgeDesk 2.0");
        root.GetProperty("body").GetString().Should().Be("Notes");
        root.GetProperty("target_commitish").GetString().Should().Be("release/2.0");
        root.GetProperty("draft").GetBoolean().Should().BeTrue();
        root.GetProperty("prerelease").GetBoolean().Should().BeFalse();
        root.GetProperty("make_latest").GetString().Should().Be("true");
    }

    [Fact]
    public async Task A_prerelease_is_never_marked_latest()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnPost(ReleasesPath, Release(6, "v2.1.0-rc.1", prerelease: true));

        await h.Service.CreateReleaseAsync(GitHubServiceHarness.Repo, new NewRelease { TagName = "v2.1.0-rc.1", Name = "", Prerelease = true }, Ct);

        using var body = JsonDocument.Parse(h.Api.Requests.Single().Body);
        body.RootElement.GetProperty("make_latest").GetString().Should().Be("false");
        body.RootElement.GetProperty("name").GetString().Should().Be("v2.1.0-rc.1", "an empty name falls back to the tag");
    }

    [Fact]
    public async Task Existing_tag_is_reported_as_invalid_input()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnError(HttpMethod.Post, ReleasesPath, (HttpStatusCode)422,
            """{"message":"Validation Failed","errors":[{"resource":"Release","code":"already_exists","field":"tag_name"}],"documentation_url":"https://docs.github.com/rest/releases/releases#create-a-release"}""");

        var act = () => h.Service.CreateReleaseAsync(GitHubServiceHarness.Repo, new NewRelease { TagName = "v1.0.0", Name = "1.0" }, Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.InvalidInput);
        error.Message.Should().Be("A release with this tag already exists.");
        error.Detail.Should().Contain("already_exists");
    }

    [Fact]
    public async Task Publishing_a_draft_clears_the_draft_flag_and_invalidates_the_list()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnGet(ReleasesPath, Array([Release(5, "v2.0.0", draft: true)]));
        h.Api.On(HttpMethod.Patch, $"{ReleasesPath}/5", _ => FakeGitHubApi.Json(Release(5, "v2.0.0")));

        await h.Service.GetReleasesAsync(GitHubServiceHarness.Repo, cancellationToken: Ct);
        var published = await h.Service.PublishReleaseAsync(GitHubServiceHarness.Repo, 5, Ct);
        await h.Service.GetReleasesAsync(GitHubServiceHarness.Repo, cancellationToken: Ct);

        published.IsDraft.Should().BeFalse();
        h.Api.RequestsTo($"{ReleasesPath}/5").Single().Body.Should().Be("""{"draft":false}""");
        h.Api.RequestsTo(ReleasesPath).Should().HaveCount(2);
    }

    [Fact]
    public async Task Asset_upload_state_is_mapped()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnGet(ReleasesPath, Array([
            Release(1, "v1.0.0", draft: true, assets: Array([Asset(10, "a.zip"), Asset(11, "b.zip").Replace("\"uploaded\"", "\"starter\"")])),
        ]));

        var release = (await h.Service.GetReleasesAsync(GitHubServiceHarness.Repo, 10, Ct)).Single();

        release.Assets.Select(a => (a.State, a.IsUploaded)).Should().Equal(("uploaded", true), ("starter", false));
    }

    [Fact]
    public async Task Updating_a_release_sends_only_the_changed_fields()
    {
        using var h = new GitHubServiceHarness();
        h.Api.On(HttpMethod.Patch, $"{ReleasesPath}/5", _ => FakeGitHubApi.Json(Release(5, "v2.0.0", draft: true)));

        var updated = await h.Service.UpdateReleaseAsync(GitHubServiceHarness.Repo, 5,
            new ReleaseChanges { Name = " v2 ", Body = "Notes", TargetCommitish = "abc1234", Prerelease = false, MakeLatest = true }, Ct);

        updated.IsDraft.Should().BeTrue();
        var body = h.Api.RequestsTo($"{ReleasesPath}/5").Single().Body;
        body.Should().Contain("\"name\":\"v2\"").And.Contain("\"body\":\"Notes\"").And.Contain("\"target_commitish\":\"abc1234\"")
            .And.Contain("\"prerelease\":false").And.Contain("\"make_latest\":\"true\"").And.NotContain("draft");
    }

    [Fact]
    public async Task Deleting_a_release_asset_sends_delete()
    {
        using var h = new GitHubServiceHarness();
        h.Api.On(HttpMethod.Delete, $"{ReleasesPath}/assets/11", _ => FakeGitHubApi.Empty(HttpStatusCode.NoContent));

        await h.Service.DeleteReleaseAssetAsync(GitHubServiceHarness.Repo, 11, Ct);

        h.Api.RequestsTo($"{ReleasesPath}/assets/11").Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Delete);
    }

    [Fact]
    public async Task Deleting_a_release_sends_delete()
    {
        using var h = new GitHubServiceHarness();
        h.Api.On(HttpMethod.Delete, $"{ReleasesPath}/5", _ => FakeGitHubApi.Empty(HttpStatusCode.NoContent));

        await h.Service.DeleteReleaseAsync(GitHubServiceHarness.Repo, 5, Ct);

        h.Api.Requests.Single().Method.Should().Be(HttpMethod.Delete);
    }

    [Fact]
    public async Task Release_notes_are_generated_between_tags()
    {
        using var h = new GitHubServiceHarness();
        h.Api.OnPost($"{ReleasesPath}/generate-notes", """{"name":"v1.1.0","body":"## What's Changed\n* Add login by @octocat"}""", HttpStatusCode.OK);

        var notes = await h.Service.GenerateReleaseNotesAsync(GitHubServiceHarness.Repo, "v1.1.0", "v1.0.0", "main", Ct);

        notes.Should().Be("## What's Changed\n* Add login by @octocat");
        using var body = JsonDocument.Parse(h.Api.Requests.Single().Body);
        body.RootElement.GetProperty("tag_name").GetString().Should().Be("v1.1.0");
        body.RootElement.GetProperty("previous_tag_name").GetString().Should().Be("v1.0.0");
        body.RootElement.GetProperty("target_commitish").GetString().Should().Be("main");
    }

    [Fact]
    public async Task Uploading_an_asset_streams_the_file_with_progress()
    {
        using var h = new GitHubServiceHarness();
        using var dir = new TempDirectory("upload");
        var content = new byte[300 * 1024];
        new Random(42).NextBytes(content);
        var file = dir.Combine("ForgeDesk-1.2.0-win-x64.zip");
        await File.WriteAllBytesAsync(file, content, Ct);
        h.Api.OnGet($"{ReleasesPath}/5", Release(5, "v1.2.0", draft: true));
        h.Api.OnPost("/repos/octo/app/releases/5/assets", Asset(77, "ForgeDesk-1.2.0-win-x64.zip", content.Length));
        var progress = new RecordingProgress<TransferProgress>();

        var asset = await h.Service.UploadReleaseAssetAsync(GitHubServiceHarness.Repo, 5, file, progress, Ct);

        asset.Id.Should().Be(77);
        asset.Size.Should().Be(content.Length);
        var upload = h.Api.RequestsTo("/repos/octo/app/releases/5/assets").Single();
        upload.Uri.Host.Should().Be("uploads.github.test");
        upload.Query.Should().Be("?name=ForgeDesk-1.2.0-win-x64.zip");
        upload.Header("Content-Type").Should().Be("application/zip");
        upload.Header("Content-Length").Should().Be(content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        upload.Header("Authorization").Should().Be($"Bearer {ClassicToken}");
        upload.RawBody.Should().Equal(content);
        progress.Reports.Should().NotBeEmpty();
        progress.Reports.Select(p => p.BytesTransferred).Should().BeInAscendingOrder();
        progress.Reports[^1].Should().Be(new TransferProgress(content.Length, content.Length));
    }

    [Fact]
    public async Task Uploading_a_missing_file_fails_before_any_request()
    {
        using var h = new GitHubServiceHarness();
        using var dir = new TempDirectory("upload");

        var act = () => h.Service.UploadReleaseAssetAsync(GitHubServiceHarness.Repo, 5, dir.Combine("missing.zip"), null, Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.PathNotFound);
        h.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Uploading_a_duplicate_asset_name_is_reported_as_invalid_input()
    {
        using var h = new GitHubServiceHarness();
        using var dir = new TempDirectory("upload");
        var file = dir.WriteFile("notes.txt", "hello");
        h.Api.OnGet($"{ReleasesPath}/5", Release(5, "v1.2.0"));
        h.Api.OnError(HttpMethod.Post, "/repos/octo/app/releases/5/assets", (HttpStatusCode)422,
            """{"message":"Validation Failed","errors":[{"resource":"ReleaseAsset","code":"already_exists","field":"name"}]}""");

        var act = () => h.Service.UploadReleaseAssetAsync(GitHubServiceHarness.Repo, 5, file, null, Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.InvalidInput);
        error.Message.Should().Be("A file with this name is already attached to the release.");
    }

    [Theory]
    [InlineData("setup.EXE", "application/vnd.microsoft.portable-executable")]
    [InlineData("app.msi", "application/x-msi")]
    [InlineData("app.tar.gz", "application/gzip")]
    [InlineData("checksums.sha256", "text/plain")]
    [InlineData("README", "application/octet-stream")]
    [InlineData("data.unknownext", "application/octet-stream")]
    public void Content_type_follows_the_extension(string fileName, string expected) =>
        ReleaseAssetContentTypes.FromFileName(fileName).Should().Be(expected);

    [Fact]
    public void Upload_url_template_is_expanded_with_an_escaped_name()
    {
        var uri = ReleaseAssetUploader.ExpandUploadUrl("https://uploads.github.com/repos/o/r/releases/1/assets{?name,label}", "My App 1.0.zip");

        uri.AbsoluteUri.Should().Be("https://uploads.github.com/repos/o/r/releases/1/assets?name=My%20App%201.0.zip");
    }

    [Fact]
    public void Upload_timeout_grows_with_the_file_size()
    {
        ReleaseAssetUploader.TimeoutFor(0).Should().Be(TimeSpan.FromMinutes(10));
        ReleaseAssetUploader.TimeoutFor(1024L * 1024 * 1024).Should().BeGreaterThan(TimeSpan.FromHours(4));
    }
}
