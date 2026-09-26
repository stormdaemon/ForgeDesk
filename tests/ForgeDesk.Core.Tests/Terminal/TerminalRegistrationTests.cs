using ForgeDesk.Core.Processes;
using ForgeDesk.Core.Terminal;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Core.Tests.Terminal;

public class TerminalRegistrationTests
{
    [Fact]
    public async Task Shell_discovery_resolves_as_a_singleton()
    {
        var services = new ServiceCollection()
            .AddSingleton<IProcessRunner>(ProcessRunner.Instance)
            .AddTerminalServices();

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        provider.GetRequiredService<IShellDiscovery>().Should().BeOfType<ShellDiscovery>()
            .And.BeSameAs(provider.GetRequiredService<IShellDiscovery>());
    }
}
