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
        var fallback = new GitCli(ProcessRunner.Instance, settings).Executable;
        Path.IsPathRooted(fallback).Should().BeTrue("the git on PATH is used, by absolute path");
        File.Exists(fallback).Should().BeTrue();
    }

    [Fact]
    public void Git_is_started_by_absolute_path_never_by_bare_name()
    {
        // A bare "git" lets Windows (and .NET on Unix) pick a git.exe planted in the current directory first.
        var executable = new GitCli(ProcessRunner.Instance).Executable;

        executable.Should().NotBeNull("git is installed on test machines");
        Path.IsPathRooted(executable).Should().BeTrue();
        Path.GetFileNameWithoutExtension(executable).Should().Be("git");
        File.Exists(executable).Should().BeTrue();
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
