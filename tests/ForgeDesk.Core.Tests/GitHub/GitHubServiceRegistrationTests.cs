using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Processes;
using ForgeDesk.Core.Security;
using ForgeDesk.Core.Settings;
using ForgeDesk.Core.Tests.GitHub.Fakes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace ForgeDesk.Core.Tests.GitHub;

public class GitHubServiceRegistrationTests
{
    [Fact]
    public void Services_resolve_as_singletons_sharing_one_session()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(new FakeClock());
        services.AddSingleton<ISecretStore>(new InMemorySecretStore());
        services.AddSingleton<ISettingsService>(new FakeSettingsService());
        services.AddSingleton(Substitute.For<IGitService>());
        services.AddSingleton(Substitute.For<IProcessRunner>());
        services.AddGitHubServices();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var accounts = provider.GetRequiredService<IGitHubAccountService>();
        var github = provider.GetRequiredService<IGitHubService>();
        var credentials = provider.GetServices<IGitCredentialProvider>().ToList();

        accounts.Should().BeSameAs(provider.GetRequiredService<IGitHubAccountService>());
        github.IsSignedIn.Should().BeFalse();
        credentials.Should().ContainSingle().Which.Should().BeOfType<GitHubCredentialProvider>();
        provider.GetRequiredService<GitHubSession>().Should().BeSameAs(provider.GetRequiredService<GitHubSession>());
    }

    [Fact]
    public void Registering_twice_does_not_duplicate_the_credential_provider()
    {
        var services = new ServiceCollection();
        services.AddGitHubServices();
        services.AddGitHubServices();

        services.Count(d => d.ServiceType == typeof(IGitCredentialProvider)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(IGitHubService)).Should().Be(1);
    }
}
