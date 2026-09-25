using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;

namespace ForgeDesk.Core.Tests.GitHub.Fakes;

/// <summary>A real <see cref="GitHubService"/> wired to <see cref="FakeGitHubApi"/> and a fake clock.</summary>
internal sealed class GitHubServiceHarness : IDisposable
{
    public static readonly GitHubRepoRef Repo = new("octo", "app");

    public GitHubServiceHarness(bool signedIn = true)
    {
        Factory = new GitHubClientFactory(new GitHubClientOptions
        {
            BaseAddress = FakeGitHubApi.BaseAddress,
            CreateHandler = () => Api,
            ProductVersion = "9.9.9",
        });
        Cache = new GitHubResponseCache(Clock);
        Clients = new GitHubClientProvider(Factory, Session);
        Service = new GitHubService(Clients, Session, Cache, new ReleaseAssetUploader(Factory));
        if (signedIn)
        {
            SignIn();
        }
    }

    public FakeGitHubApi Api { get; } = new();

    public FakeClock Clock { get; } = new();

    public GitHubSession Session { get; } = new();

    public GitHubClientFactory Factory { get; }

    public GitHubClientProvider Clients { get; }

    public GitHubResponseCache Cache { get; }

    public GitHubService Service { get; }

    public void SignIn(string token = GitHubPayloads.ClassicToken, string login = "octocat") =>
        Session.Set(token, new GitHubAccount(new GitHubUser(login, null, string.Empty, $"https://github.com/{login}"), ["repo", "workflow"], GitHubAuthMethod.PersonalAccessToken));

    public void Dispose()
    {
        Service.Dispose();
        Factory.Dispose();
    }
}
