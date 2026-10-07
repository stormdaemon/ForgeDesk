using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Settings;
using ForgeDesk.Presentation.Dashboard;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Tests.Settings.Support;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Dashboard;

public sealed class CloneRepositoryDialogViewModelTests : IDisposable
{
    private static readonly GitHubAccount Account = new(new GitHubUser("octo", "Octo Cat", "https://avatars/octo", "https://github.com/octo"), ["repo"],
        GitHubAuthMethod.GitCredentialManager);

    private readonly IGitHubService _github = Substitute.For<IGitHubService>();
    private readonly IGitHubAccountService _accounts = Substitute.For<IGitHubAccountService>();
    private readonly IProjectCloneService _clones = Substitute.For<IProjectCloneService>();
    private readonly IProjectRegistry _registry = Substitute.For<IProjectRegistry>();
    private readonly FakeSettingsService _settings;
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly INavigationService _navigation = Substitute.For<INavigationService>();
    private readonly IShellIntegration _shell = Substitute.For<IShellIntegration>();
    private readonly TestFolder _folder = new();
    private readonly List<bool?> _closed = [];
    private CloneRepositoryDialogViewModel? _dialog;

    public CloneRepositoryDialogViewModelTests()
    {
        _settings = new FakeSettingsService(AppSettings.Default with { DefaultCloneDirectory = _folder.Path });
        _registry.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<Project>());
    }

    public void Dispose()
    {
        _dialog?.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public void Signed_out_it_opens_on_the_url_tab_and_offers_to_sign_in()
    {
        var dialog = Create();

        dialog.IsUrlSource.Should().BeTrue();
        dialog.ShowSignInPrompt.Should().BeTrue();
        dialog.BaseFolder.Should().Be(_folder.Path);
        dialog.CanClone.Should().BeFalse();
        dialog.DestinationError.Should().BeNull("nothing is chosen yet");
    }

    [Fact]
    public void Deselecting_the_tab_keeps_the_current_one()
    {
        var dialog = Create();

        dialog.SelectedTab = null!;

        dialog.IsUrlSource.Should().BeTrue();
    }

    [Fact]
    public void Sign_in_closes_the_dialog_and_opens_settings_github()
    {
        var dialog = Create();

        dialog.SignInCommand.Execute(null);

        _closed.Should().Equal(false);
        _navigation.Received(1).OpenSettings("GitHub");
    }

    [Fact]
    public async Task Signed_in_it_lists_the_repositories_newest_first_and_marks_those_already_added()
    {
        SignIn();
        _github.GetMyRepositoriesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            Repo("octo", "old", pushed: TestData.Now.AddDays(-30)),
            Repo("octo", "fresh", pushed: TestData.Now, language: "Rust"),
            Repo("team", "shared", pushed: TestData.Now.AddDays(-2), isPrivate: true),
        ]);
        _registry.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([TestData.Project("shared", "/dev/shared", gitHub: new GitHubRepoRef("team", "shared"))]);
        var dialog = Create();

        dialog.IsGitHubSource.Should().BeTrue();
        dialog.Start();
        await WaitUntil(() => dialog.HasLoadedRepositories);

        dialog.Repositories.Select(r => r.FullName).Should().Equal("octo/fresh", "team/shared", "octo/old");
        dialog.Repositories.Single(r => r.Name == "shared").IsAlreadyAdded.Should().BeTrue();
        dialog.ShowRepositoryList.Should().BeTrue();

        dialog.RepositoryFilter = "rust";

        dialog.Repositories.Select(r => r.Name).Should().Equal("fresh");

        dialog.RepositoryFilter = "zzz";

        dialog.ShowNoRepositoryMatches.Should().BeTrue();
    }

    [Fact]
    public async Task A_listing_failure_is_shown_with_retry()
    {
        SignIn();
        _github.GetMyRepositoriesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<GitHubRepository>>(new ForgeException(ErrorKind.NetworkUnavailable, "GitHub can't be reached.")));
        var dialog = Create();

        dialog.Start();
        await WaitUntil(() => !dialog.IsLoadingRepositories);

        dialog.RepositoriesError!.Message.Should().Be("GitHub can't be reached.");
        dialog.ShowRepositoryList.Should().BeFalse();
    }

    [Fact]
    public async Task Choosing_a_repository_suggests_its_name_until_the_user_types_one()
    {
        SignIn();
        _github.GetMyRepositoriesAsync(Arg.Any<CancellationToken>()).Returns([Repo("octo", "alpha"), Repo("octo", "beta")]);
        var dialog = Create();
        dialog.Start();
        await WaitUntil(() => dialog.HasLoadedRepositories);

        dialog.SelectedRepository = dialog.Repositories.Single(r => r.Name == "alpha");

        dialog.FolderName.Should().Be("alpha");
        dialog.TargetPath.Should().Be(Path.Combine(_folder.Path, "alpha"));
        dialog.SourceUrl.Should().Be("https://github.com/octo/alpha.git");
        dialog.CanClone.Should().BeTrue();

        dialog.FolderName = "my-alpha";
        dialog.SelectedRepository = dialog.Repositories.Single(r => r.Name == "beta");

        dialog.FolderName.Should().Be("my-alpha");

        dialog.FolderName = string.Empty;
        dialog.SelectedRepository = dialog.Repositories.Single(r => r.Name == "alpha");

        dialog.FolderName.Should().Be("alpha");
    }

    [Fact]
    public void A_typed_url_is_validated_and_recognized_as_github()
    {
        var dialog = Create();

        dialog.Url = "not a url";

        dialog.UrlError.Should().NotBeNull();
        dialog.CanClone.Should().BeFalse();
        dialog.FolderName.Should().BeEmpty();

        dialog.Url = "git@github.com:stormdaemon/ForgeDesk.git";

        dialog.UrlError.Should().BeNull();
        dialog.ParsedGitHubRepo!.FullName.Should().Be("stormdaemon/ForgeDesk");
        dialog.FolderName.Should().Be("ForgeDesk");
        dialog.SourceSummary.Should().Be("stormdaemon/ForgeDesk");
        dialog.CanClone.Should().BeTrue();
    }

    [Fact]
    public void An_existing_non_empty_folder_blocks_the_clone()
    {
        Directory.CreateDirectory(_folder.Combine("repo"));
        File.WriteAllText(_folder.Combine("repo", "file.txt"), "x");
        var dialog = Create();

        dialog.Url = "https://github.com/owner/repo.git";

        dialog.DestinationError.Should().Contain("already exists");
        dialog.CanClone.Should().BeFalse();
        dialog.CloneCommand.CanExecute(null).Should().BeFalse();

        dialog.FolderName = "repo-2";

        dialog.DestinationError.Should().BeNull();
        dialog.CanClone.Should().BeTrue();
    }

    [Fact]
    public async Task Changing_the_base_folder_uses_the_picker()
    {
        var other = _folder.Combine("elsewhere");
        Directory.CreateDirectory(other);
        _dialogs.PickFolderAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(other);
        var dialog = Create();

        await dialog.ChangeBaseFolderCommand.ExecuteAsync(null);

        dialog.BaseFolder.Should().Be(other);
    }

    [Fact]
    public async Task A_successful_clone_reports_progress_remembers_the_folder_and_closes()
    {
        var elsewhere = _folder.Combine("clones");
        _settings.ChangeExternally(s => s with { DefaultCloneDirectory = null });
        var project = TestData.Project("repo", Path.Combine(elsewhere, "repo"));
        var progressSeen = new List<double>();
        _clones.CloneAsync("https://github.com/owner/repo.git", Path.Combine(elsewhere, "repo"), Arg.Any<IProgress<GitProgress>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var progress = call.Arg<IProgress<GitProgress>>();
                progress.Report(new GitProgress("Receiving objects", 45, "Receiving objects:  45% (45/100)"));
                progressSeen.Add(_dialog!.ProgressPercent);
                _dialog.ProgressStage.Should().Be("Receiving objects");
                _dialog.IsProgressIndeterminate.Should().BeFalse();
                _dialog.IsCloning.Should().BeTrue();
                return project;
            });
        var dialog = Create();
        dialog.BaseFolder = elsewhere;
        dialog.Url = "https://github.com/owner/repo.git";

        await dialog.CloneCommand.ExecuteAsync(null);

        progressSeen.Should().Equal(45);
        dialog.ClonedProject.Should().Be(project);
        _closed.Should().Equal(true);
        _settings.Current.DefaultCloneDirectory.Should().Be(elsewhere);
        dialog.IsCloning.Should().BeFalse();
    }

    [Fact]
    public async Task An_authentication_failure_explains_how_to_sign_in()
    {
        _clones.CloneAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<GitProgress>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Project>(new ForgeException(ErrorKind.AuthenticationFailed, "Authentication failed for github.com.",
                null, "fatal: Authentication failed")));
        var dialog = Create();
        dialog.Url = "https://github.com/owner/private.git";

        await dialog.CloneCommand.ExecuteAsync(null);

        dialog.CloneError!.Message.Should().Be("Authentication failed for github.com.");
        dialog.CloneError.Hint.Should().Contain("sign in to GitHub").And.Contain("Git Credential Manager");
        dialog.CloneError.Detail.Should().Be("fatal: Authentication failed");
        dialog.IsAuthenticationError.Should().BeTrue();
        _closed.Should().BeEmpty();
        dialog.CanClone.Should().BeTrue("the user can try again");
    }

    [Fact]
    public async Task Cancel_stops_a_running_clone_and_keeps_the_dialog_open()
    {
        var started = new TaskCompletionSource();
        _clones.CloneAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<GitProgress>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                return TestData.Project("never", "/never");
            });
        var dialog = Create();
        dialog.Url = "https://github.com/owner/repo.git";

        var clone = dialog.CloneCommand.ExecuteAsync(null);
        await started.Task;
        dialog.CancelText.Should().Be("Cancel clone");
        dialog.CancelCommand.Execute(null);
        await clone;

        dialog.IsCloning.Should().BeFalse();
        dialog.StatusText.Should().Contain("cancelled");
        dialog.CloneError.Should().BeNull();
        _closed.Should().BeEmpty();

        dialog.CancelCommand.Execute(null);

        _closed.Should().Equal(false);
    }

    [Fact]
    public async Task Closing_the_window_cancels_the_clone()
    {
        CancellationToken seen = default;
        var started = new TaskCompletionSource();
        _clones.CloneAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<GitProgress>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                seen = call.Arg<CancellationToken>();
                started.SetResult();
                await Task.Delay(Timeout.Infinite, seen);
                return TestData.Project("never", "/never");
            });
        var dialog = Create();
        dialog.Url = "https://github.com/owner/repo.git";

        var clone = dialog.CloneCommand.ExecuteAsync(null);
        await started.Task;
        dialog.OnDialogClosed();
        await clone;

        seen.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void Signing_in_while_the_dialog_is_open_switches_the_list_on()
    {
        _github.GetMyRepositoriesAsync(Arg.Any<CancellationToken>()).Returns([Repo("octo", "alpha")]);
        var dialog = Create();
        dialog.Start();
        dialog.SelectedTab = dialog.Tabs[0];
        dialog.ShowSignInPrompt.Should().BeTrue();

        _accounts.AccountChanged += Raise.Event<EventHandler<GitHubAccount?>>(_accounts, Account);

        dialog.IsSignedIn.Should().BeTrue();
        dialog.ShowSignInPrompt.Should().BeFalse();
        _github.Received(1).GetMyRepositoriesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ErrorKind.AuthenticationRequired, true)]
    [InlineData(ErrorKind.NotFound, false)]
    [InlineData(ErrorKind.GitCommandFailed, false)]
    public void Errors_carry_a_useful_hint(ErrorKind kind, bool mentionsSignIn)
    {
        var error = CloneRepositoryDialogViewModel.Describe(new ForgeException(kind, "Failed."));

        error.Title.Should().Be("Could not clone the repository");
        if (mentionsSignIn)
        {
            error.Hint.Should().Contain("sign in");
        }

        if (kind == ErrorKind.NotFound)
        {
            error.Hint.Should().Contain("private");
        }
    }

    private CloneRepositoryDialogViewModel Create()
    {
        _dialog = new CloneRepositoryDialogViewModel(_github, _accounts, _clones, _registry, _settings, _dialogs, _navigation, _shell,
            ImmediateDispatcher.Instance, NullLogger<CloneRepositoryDialogViewModel>.Instance);
        _dialog.CloseRequested += (_, result) => _closed.Add(result);
        return _dialog;
    }

    private void SignIn() => _accounts.Current.Returns(Account);

    private static GitHubRepository Repo(string owner, string name, DateTimeOffset? pushed = null, string? language = null, bool isPrivate = false) => new()
    {
        Owner = owner,
        Name = name,
        HtmlUrl = $"https://github.com/{owner}/{name}",
        CloneUrl = $"https://github.com/{owner}/{name}.git",
        PushedAt = pushed ?? TestData.Now,
        Language = language,
        IsPrivate = isPrivate,
    };

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        condition().Should().BeTrue();
    }
}
