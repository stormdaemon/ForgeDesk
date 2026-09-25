using ForgeDesk.Core.Common;
using ForgeDesk.Core.Files;
using ForgeDesk.Core.Processes;
using ForgeDesk.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace ForgeDesk.Core.Tests.Files;

public class FilesRegistrationTests
{
    [Fact]
    public async Task Files_services_resolve_as_singletons()
    {
        var services = new ServiceCollection()
            .AddSingleton<IProcessRunner>(ProcessRunner.Instance)
            .AddSingleton<IClock>(SystemClock.Instance)
            .AddSingleton(Substitute.For<ISettingsService>())
            .AddFilesServices();

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        provider.GetRequiredService<IFileService>().Should().BeOfType<FileService>();
        provider.GetRequiredService<IFileIndex>().Should().BeOfType<FileIndex>();
        provider.GetRequiredService<IContentSearchService>().Should().BeOfType<ContentSearchService>();
        provider.GetRequiredService<IProjectWatcher>().Should().BeOfType<ProjectWatcher>()
            .Which.Debounce.Should().Be(ProjectWatcher.DefaultDebounce);
        provider.GetRequiredService<IFileIndex>().Should().BeSameAs(provider.GetRequiredService<IFileIndex>());
    }

    [Fact]
    public void Configured_git_executable_is_used_when_it_exists()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(AppSettings.Default with { GitExecutablePath = typeof(FilesRegistrationTests).Assembly.Location });
        new GitCli(ProcessRunner.Instance, settings).Executable.Should().Be(typeof(FilesRegistrationTests).Assembly.Location);

        settings.Current.Returns(AppSettings.Default with { GitExecutablePath = "/definitely/missing/git" });
        new GitCli(ProcessRunner.Instance, settings).Executable.Should().Be("git");
    }

    [Theory]
    [InlineData("plain/path.txt", "plain/path.txt")]
    [InlineData("\"tab\\there.txt\"", "tab\there.txt")]
    [InlineData("\"quote\\\"d.txt\"", "quote\"d.txt")]
    [InlineData("\"new\\nline.txt\"", "new\nline.txt")]
    [InlineData("\"caf\\303\\251.txt\"", "café.txt")]
    [InlineData("\"back\\\\slash.txt\"", "back\\slash.txt")]
    public void Unquotes_git_c_style_paths(string raw, string expected)
    {
        GitCli.UnquotePath(raw).Should().Be(expected);
    }
}
