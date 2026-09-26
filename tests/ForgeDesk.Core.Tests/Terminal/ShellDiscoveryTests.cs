using System.Text;
using ForgeDesk.Core.Processes;
using ForgeDesk.Core.Terminal;

namespace ForgeDesk.Core.Tests.Terminal;

public class ShellDiscoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A simulated machine: environment variables, files on disk, PATH lookups and program outputs.</summary>
    private sealed class FakeEnvironment(bool isWindows) : IShellEnvironment
    {
        public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> PathEntries { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string?> Outputs { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<(string File, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string?> Environment)> Invocations { get; } = [];

        public bool IsWindows => isWindows;

        public string? GetEnvironmentVariable(string name) => Variables.GetValueOrDefault(name);

        public bool FileExists(string path) => Files.Contains(path);

        public string? FindInPath(string name) => PathEntries.GetValueOrDefault(name);

        public Task<string?> CaptureOutputAsync(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken)
        {
            Invocations.Add((fileName, arguments, environment));
            return Task.FromResult(Outputs.GetValueOrDefault(fileName));
        }
    }

    private static FakeEnvironment StandardWindows()
    {
        var env = new FakeEnvironment(isWindows: true);
        env.Variables["SystemRoot"] = @"C:\Windows";
        env.Variables["ProgramFiles"] = @"C:\Program Files";
        env.Variables["ProgramW6432"] = @"C:\Program Files";
        env.Variables["ProgramFiles(x86)"] = @"C:\Program Files (x86)";
        env.Variables["LOCALAPPDATA"] = @"C:\Users\ada\AppData\Local";
        env.Variables["ComSpec"] = @"C:\Windows\system32\cmd.exe";
        env.Files.Add(@"C:\Windows\system32\cmd.exe");
        env.Files.Add(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe");
        return env;
    }

    /// <summary>What the process runner hands back for UTF-16LE output: each character followed by NUL, lines split on CR and LF.</summary>
    private static string AsDecodedByProcessRunner(string utf16Text)
    {
        var decoded = Encoding.UTF8.GetString([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(utf16Text)]);
        return string.Join('\n', decoded.Split(["\r\n", "\r", "\n"], StringSplitOptions.None)) + "\n";
    }

    [Fact]
    public async Task Finds_every_windows_shell_in_preferred_order()
    {
        var env = StandardWindows();
        env.Files.Add(@"C:\Program Files\PowerShell\7\pwsh.exe");
        env.Files.Add(@"C:\Program Files\Git\bin\bash.exe");
        env.Files.Add(@"C:\Windows\System32\wsl.exe");
        env.Outputs[@"C:\Windows\System32\wsl.exe"] = AsDecodedByProcessRunner("Ubuntu-22.04\r\ndocker-desktop\r\ndocker-desktop-data\r\nDebian\r\n");

        var profiles = await new ShellDiscovery(env).DiscoverAsync(Ct);

        profiles.Select(p => p.Id).Should().Equal("pwsh", "powershell", "cmd", "git-bash", "wsl:Ubuntu-22.04", "wsl:Debian");
        profiles[0].Should().Be(new ShellProfile("pwsh", "PowerShell", ShellKind.PowerShellCore, @"C:\Program Files\PowerShell\7\pwsh.exe", "-NoLogo"));
        profiles[1].Should().Be(new ShellProfile("powershell", "Windows PowerShell", ShellKind.WindowsPowerShell, @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "-NoLogo"));
        profiles[2].Should().Be(new ShellProfile("cmd", "Command Prompt", ShellKind.CommandPrompt, @"C:\Windows\system32\cmd.exe", ""));
        profiles[3].Should().Be(new ShellProfile("git-bash", "Git Bash", ShellKind.GitBash, @"C:\Program Files\Git\bin\bash.exe", "--login -i"));
        profiles[4].Should().Be(new ShellProfile("wsl:Ubuntu-22.04", "Ubuntu-22.04 (WSL)", ShellKind.Wsl, @"C:\Windows\System32\wsl.exe", "-d Ubuntu-22.04"));

        var wslCall = env.Invocations.Should().ContainSingle().Subject;
        wslCall.Arguments.Should().Equal("-l", "-q");
        wslCall.Environment.Should().ContainKey("WSL_UTF8");
    }

    [Fact]
    public async Task Command_lines_quote_paths_with_spaces()
    {
        var env = StandardWindows();
        env.Files.Add(@"C:\Program Files\PowerShell\7\pwsh.exe");

        var profiles = await new ShellDiscovery(env).DiscoverAsync(Ct);

        profiles[0].CommandLine.Should().Be("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoLogo");
    }

    [Fact]
    public async Task Prefers_pwsh_from_path()
    {
        var env = StandardWindows();
        env.PathEntries["pwsh"] = @"C:\Users\ada\AppData\Local\Microsoft\WindowsApps\pwsh.exe";

        var profiles = await new ShellDiscovery(env).DiscoverAsync(Ct);

        profiles[0].Executable.Should().Be(@"C:\Users\ada\AppData\Local\Microsoft\WindowsApps\pwsh.exe");
    }

    [Fact]
    public async Task Minimal_windows_has_windows_powershell_and_cmd()
    {
        var profiles = await new ShellDiscovery(StandardWindows()).DiscoverAsync(Ct);

        profiles.Select(p => p.Id).Should().Equal("powershell", "cmd");
    }

    [Fact]
    public async Task Falls_back_to_system32_cmd_when_comspec_is_missing()
    {
        var env = StandardWindows();
        env.Variables.Remove("ComSpec");
        env.Files.Add(@"C:\Windows\System32\cmd.exe");

        var profiles = await new ShellDiscovery(env).DiscoverAsync(Ct);

        profiles.Single(p => p.Id == "cmd").Executable.Should().Be(@"C:\Windows\System32\cmd.exe");
    }

    [Theory]
    [InlineData(@"C:\Users\ada\AppData\Local\Programs\Git\bin\bash.exe", null)]
    [InlineData(@"D:\Tools\PortableGit\bin\bash.exe", @"D:\Tools\PortableGit\cmd\git.exe")]
    [InlineData(@"C:\Program Files (x86)\Git\bin\bash.exe", null)]
    public async Task Finds_git_bash_in_user_portable_and_32_bit_installs(string bash, string? gitInPath)
    {
        var env = StandardWindows();
        env.Files.Add(bash);
        if (gitInPath is not null)
        {
            env.PathEntries["git"] = gitInPath;
        }

        var profiles = await new ShellDiscovery(env).DiscoverAsync(Ct);

        profiles.Single(p => p.Id == "git-bash").Executable.Should().Be(bash);
    }

    [Fact]
    public async Task Wsl_without_distributions_adds_nothing()
    {
        var env = StandardWindows();
        env.Files.Add(@"C:\Windows\System32\wsl.exe");
        env.Outputs[@"C:\Windows\System32\wsl.exe"] = null;

        var profiles = await new ShellDiscovery(env).DiscoverAsync(Ct);

        profiles.Should().NotContain(p => p.Kind == ShellKind.Wsl);
    }

    [Fact]
    public async Task Unix_lists_the_login_shell_first_without_duplicates()
    {
        var env = new FakeEnvironment(isWindows: false);
        env.Variables["SHELL"] = "/usr/bin/zsh";
        env.Files.UnionWith(["/usr/bin/zsh", "/bin/bash", "/bin/sh"]);
        env.PathEntries["bash"] = "/bin/bash";
        env.PathEntries["zsh"] = "/usr/bin/zsh";
        env.PathEntries["sh"] = "/bin/sh";

        var profiles = await new ShellDiscovery(env).DiscoverAsync(Ct);

        profiles.Should().Equal(
            new ShellProfile("zsh", "Zsh", ShellKind.Unix, "/usr/bin/zsh", ""),
            new ShellProfile("bash", "Bash", ShellKind.Unix, "/bin/bash", ""),
            new ShellProfile("sh", "sh", ShellKind.Unix, "/bin/sh", ""));
    }

    [Fact]
    public async Task Real_machine_discovery_returns_usable_shells()
    {
        // WSL is left out: starting its service on a cold CI machine can take several seconds.
        var profiles = await new ShellDiscovery(new WithoutPrograms(new SystemShellEnvironment(ProcessRunner.Instance))).DiscoverAsync(Ct);

        profiles.Should().NotBeEmpty();
        profiles.Select(p => p.Id).Should().OnlyHaveUniqueItems();
        profiles.Should().OnlyContain(p => File.Exists(p.Executable));
    }

    [Fact]
    public async Task System_environment_captures_program_output()
    {
        var environment = new SystemShellEnvironment(ProcessRunner.Instance);
        var (file, arguments) = OperatingSystem.IsWindows()
            ? (Path.Combine(Environment.SystemDirectory, "cmd.exe"), new[] { "/d", "/c", "echo %PROBE_VALUE%" })
            : ("/bin/sh", new[] { "-c", "echo $PROBE_VALUE" });

        var output = await environment.CaptureOutputAsync(file, arguments, new Dictionary<string, string?> { ["PROBE_VALUE"] = "probed" }, Ct);
        var missing = await environment.CaptureOutputAsync("definitely-not-a-shell-xyz", [], new Dictionary<string, string?>(), Ct);

        output!.Trim().Should().Be("probed");
        missing.Should().BeNull();
    }

    /// <summary>The real machine, minus running programs.</summary>
    private sealed class WithoutPrograms(IShellEnvironment inner) : IShellEnvironment
    {
        public bool IsWindows => inner.IsWindows;

        public string? GetEnvironmentVariable(string name) => inner.GetEnvironmentVariable(name);

        public bool FileExists(string path) => inner.FileExists(path);

        public string? FindInPath(string name) => inner.FindInPath(name);

        public Task<string?> CaptureOutputAsync(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
    }
}

public class WslDistributionsTests
{
    [Fact]
    public void Parses_utf16_output_read_as_utf8()
    {
        var raw = Encoding.UTF8.GetString([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("Ubuntu\r\nopenSUSE-Tumbleweed\r\n")]);

        WslDistributions.Parse(raw).Should().Equal("Ubuntu", "openSUSE-Tumbleweed");
    }

    [Fact]
    public void Parses_utf8_output()
    {
        WslDistributions.Parse("Ubuntu\nkali-linux\n").Should().Equal("Ubuntu", "kali-linux");
    }

    [Fact]
    public void Skips_docker_distributions_messages_and_duplicates()
    {
        var output = "docker-desktop\r\nDocker-Desktop-Data\r\nUbuntu\r\nubuntu\r\nWindows Subsystem for Linux has no installed distributions.\r\n";

        WslDistributions.Parse(output).Should().Equal("Ubuntu");
    }

    [Fact]
    public void Empty_output_yields_nothing()
    {
        WslDistributions.Parse(null).Should().BeEmpty();
        WslDistributions.Parse("\0\r\0\n\0").Should().BeEmpty();
    }
}
