using System.Text;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Files;
using ForgeDesk.Core.Processes;
using ForgeDesk.Core.Tests.Infrastructure;

namespace ForgeDesk.Core.Tests.Files;

public class FileServiceTests
{
    private static readonly FileService Service = new(new GitCli(ProcessRunner.Instance));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("node_modules", true)]
    [InlineData("Node_Modules", true)]
    [InlineData(".git", true)]
    [InlineData("bin", true)]
    [InlineData("OBJ", true)]
    [InlineData("__pycache__", true)]
    [InlineData(".venv", true)]
    [InlineData("src", false)]
    [InlineData("binaries", false)]
    [InlineData("", false)]
    public void Recognizes_heavy_folder_names(string name, bool expected)
    {
        Service.IsHeavyFolderName(name).Should().Be(expected);
    }

    [Fact]
    public async Task Lists_directories_first_then_files_in_natural_order()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("file10.txt", "x");
        dir.WriteFile("file2.txt", "hello");
        dir.WriteFile("File1.txt", "x");
        dir.WriteFile("zeta/a.txt", "x");
        dir.WriteFile("Alpha/a.txt", "x");
        dir.WriteFile(".env", "SECRET=1");
        Directory.CreateDirectory(dir.Combine("node_modules"));

        var entries = await Service.ListDirectoryAsync(dir.Path, "", Ct);

        entries.Select(e => e.Name).Should().Equal("Alpha", "node_modules", "zeta", ".env", "File1.txt", "file2.txt", "file10.txt");
        entries.Single(e => e.Name == "node_modules").IsHeavyFolder.Should().BeTrue();
        entries.Single(e => e.Name == "Alpha").Should().Match<FileEntry>(e => e.IsDirectory && !e.IsHeavyFolder && e.RelativePath == "Alpha");
        entries.Single(e => e.Name == ".env").IsHidden.Should().BeTrue();
        var file2 = entries.Single(e => e.Name == "file2.txt");
        file2.Size.Should().Be(5);
        file2.IsHidden.Should().BeFalse();
        file2.LastModified.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
        entries.Should().OnlyContain(e => !e.IsIgnored, "the folder is not a git repository");
    }

    [Fact]
    public async Task Lists_a_subdirectory_with_root_relative_paths()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("src/app/main.cs", "x");

        var entries = await Service.ListDirectoryAsync(dir.Path, "src/app", Ct);

        entries.Should().ContainSingle().Which.RelativePath.Should().Be("src/app/main.cs");
    }

    [Fact]
    public async Task Marks_git_ignored_entries()
    {
        using var repo = TestRepository.Create();
        repo.WriteFile(".gitignore", "*.log\nout/\n");
        repo.WriteFile("app.log", "x");
        repo.WriteFile("out/result.txt", "x");
        repo.WriteFile("src/main.cs", "x");

        var entries = await Service.ListDirectoryAsync(repo.Path, "", Ct);

        entries.Single(e => e.Name == "app.log").IsIgnored.Should().BeTrue();
        entries.Single(e => e.Name == "out").IsIgnored.Should().BeTrue();
        entries.Single(e => e.Name == "src").IsIgnored.Should().BeFalse();
        entries.Single(e => e.Name == "README.md").IsIgnored.Should().BeFalse();
    }

    [Fact]
    public async Task Flags_symbolic_links_without_following_them()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("target/inside.txt", "x");
        try
        {
            Directory.CreateSymbolicLink(dir.Combine("link"), dir.Combine("target"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Skip("Creating symbolic links requires Developer Mode or elevation on this machine.");
        }

        var entries = await Service.ListDirectoryAsync(dir.Path, "", Ct);

        entries.Single(e => e.Name == "link").IsSymbolicLink.Should().BeTrue();
        entries.Single(e => e.Name == "target").IsSymbolicLink.Should().BeFalse();
    }

    [Fact]
    public async Task Missing_directory_is_path_not_found()
    {
        using var dir = new TempDirectory();
        var act = () => Service.ListDirectoryAsync(dir.Path, "nope", Ct);
        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.PathNotFound);
    }

    [Fact]
    public async Task Listing_outside_the_project_is_rejected()
    {
        using var dir = new TempDirectory();
        var act = () => Service.ListDirectoryAsync(dir.Path, "../..", Ct);
        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
    }

    [Fact]
    public async Task Listing_a_file_is_invalid_input()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("a.txt", "x");
        var act = () => Service.ListDirectoryAsync(dir.Path, "a.txt", Ct);
        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
    }

    [Fact]
    public async Task Reads_utf8_text_with_line_information()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("a.txt", "héllo\nwörld\n");

        var content = await Service.ReadAsync(dir.Path, "a.txt", cancellationToken: Ct);

        content.Kind.Should().Be(FileContentKind.Text);
        content.Text.Should().Be("héllo\nwörld\n");
        content.EncodingName.Should().Be("UTF-8");
        content.LineEndings.Should().Be("LF");
        content.LineCount.Should().Be(2);
        content.IsTruncated.Should().BeFalse();
        content.RelativePath.Should().Be("a.txt");
        content.Size.Should().Be(Encoding.UTF8.GetByteCount("héllo\nwörld\n"));
    }

    [Theory]
    [InlineData("utf-8-bom", "UTF-8 with BOM")]
    [InlineData("utf-16le", "UTF-16 LE")]
    [InlineData("utf-16be", "UTF-16 BE")]
    [InlineData("utf-32le", "UTF-32 LE")]
    [InlineData("utf-32be", "UTF-32 BE")]
    public async Task Decodes_files_with_a_byte_order_mark(string encodingName, string expectedName)
    {
        Encoding encoding = encodingName switch
        {
            "utf-8-bom" => new UTF8Encoding(true),
            "utf-16le" => new UnicodeEncoding(false, true),
            "utf-16be" => new UnicodeEncoding(true, true),
            "utf-32le" => new UTF32Encoding(false, true),
            _ => new UTF32Encoding(true, true),
        };
        using var dir = new TempDirectory();
        const string text = "Ünïcödé ✓\r\nline two\r\n";
        await File.WriteAllBytesAsync(dir.Combine("f.txt"), [.. encoding.GetPreamble(), .. encoding.GetBytes(text)], Ct);

        var content = await Service.ReadAsync(dir.Path, "f.txt", cancellationToken: Ct);

        content.Kind.Should().Be(FileContentKind.Text);
        content.Text.Should().Be(text);
        content.EncodingName.Should().Be(expectedName);
        content.LineEndings.Should().Be("CRLF");
        content.LineCount.Should().Be(2);
    }

    [Fact]
    public async Task Invalid_utf8_falls_back_to_windows_1252()
    {
        using var dir = new TempDirectory();
        // "café €" in Windows-1252: é = 0xE9, € = 0x80.
        await File.WriteAllBytesAsync(dir.Combine("legacy.txt"), [0x63, 0x61, 0x66, 0xE9, 0x20, 0x80, 0x0D, 0x0A], Ct);

        var content = await Service.ReadAsync(dir.Path, "legacy.txt", cancellationToken: Ct);

        content.Kind.Should().Be(FileContentKind.Text);
        content.EncodingName.Should().BeOneOf("Windows-1252", "Latin-1");
        content.Text.Should().StartWith("café ");
        if (content.EncodingName == "Windows-1252")
        {
            content.Text.Should().Be("café €\r\n");
        }
    }

    [Fact]
    public async Task Detects_binary_content_by_nul_bytes()
    {
        using var dir = new TempDirectory();
        await File.WriteAllBytesAsync(dir.Combine("data.dat"), [0x41, 0x42, 0x00, 0x43, 0x44], Ct);

        var content = await Service.ReadAsync(dir.Path, "data.dat", cancellationToken: Ct);

        content.Kind.Should().Be(FileContentKind.Binary);
        content.Text.Should().BeNull();
        content.Size.Should().Be(5);
    }

    [Fact]
    public async Task Large_binary_files_are_detected_without_reading_everything()
    {
        using var dir = new TempDirectory();
        var bytes = new byte[3 * 1024 * 1024];
        bytes[100] = 0;
        Array.Fill(bytes, (byte)'a', 0, 100);
        await File.WriteAllBytesAsync(dir.Combine("blob.data"), bytes, Ct);

        var content = await Service.ReadAsync(dir.Path, "blob.data", cancellationToken: Ct);

        content.Kind.Should().Be(FileContentKind.Binary);
    }

    [Theory]
    [InlineData("logo.png", FileContentKind.Image)]
    [InlineData("photo.JPG", FileContentKind.Image)]
    [InlineData("icon.ico", FileContentKind.Image)]
    [InlineData("pic.webp", FileContentKind.Image)]
    [InlineData("scan.tiff", FileContentKind.Image)]
    [InlineData("app.exe", FileContentKind.Binary)]
    [InlineData("lib.dll", FileContentKind.Binary)]
    [InlineData("archive.zip", FileContentKind.Binary)]
    public async Task Classifies_known_extensions_without_decoding(string name, FileContentKind expected)
    {
        using var dir = new TempDirectory();
        dir.WriteFile(name, "not really an image");

        var content = await Service.ReadAsync(dir.Path, name, cancellationToken: Ct);

        content.Kind.Should().Be(expected);
        content.Text.Should().BeNull();
    }

    [Fact]
    public async Task Truncates_large_text_on_a_line_boundary()
    {
        using var dir = new TempDirectory();
        var text = string.Concat(Enumerable.Range(0, 100).Select(i => $"line {i:000}\n"));
        dir.WriteFile("big.txt", text);

        var content = await Service.ReadAsync(dir.Path, "big.txt", maxTextBytes: 100, Ct);

        // Eleven 9-byte lines fit in 100 bytes; the twelfth would be cut, so it is dropped entirely.
        content.IsTruncated.Should().BeTrue();
        content.Text.Should().Be(text[..99]);
        content.LineCount.Should().Be(11);
        content.Size.Should().Be(text.Length);
    }

    [Fact]
    public async Task Truncation_never_splits_a_multibyte_character()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("one-line.txt", new string('é', 100));

        var content = await Service.ReadAsync(dir.Path, "one-line.txt", maxTextBytes: 51, Ct);

        content.IsTruncated.Should().BeTrue();
        content.EncodingName.Should().Be("UTF-8");
        content.Text.Should().Be(new string('é', 25));
    }

    [Fact]
    public async Task Exact_size_limit_is_not_truncated()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("exact.txt", "12345\n");

        var content = await Service.ReadAsync(dir.Path, "exact.txt", maxTextBytes: 6, Ct);

        content.IsTruncated.Should().BeFalse();
        content.Text.Should().Be("12345\n");
    }

    [Theory]
    [InlineData("a\r\nb\r\n", "CRLF", 2)]
    [InlineData("a\nb", "LF", 2)]
    [InlineData("a\r\nb\nc", "Mixed", 3)]
    [InlineData("single line", null, 1)]
    [InlineData("", null, 0)]
    public void Detects_line_endings_and_counts_lines(string text, string? endings, int lines)
    {
        FileService.DetectLineEndings(text).Should().Be(endings);
        FileService.CountLines(text).Should().Be(lines);
    }

    [Fact]
    public async Task Empty_file_is_text()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("empty.txt", "");

        var content = await Service.ReadAsync(dir.Path, "empty.txt", cancellationToken: Ct);

        content.Kind.Should().Be(FileContentKind.Text);
        content.Text.Should().BeEmpty();
        content.LineCount.Should().Be(0);
    }

    [Fact]
    public async Task Reading_outside_the_project_is_rejected()
    {
        using var dir = new TempDirectory();
        var act = () => Service.ReadAsync(dir.Path, "../../etc/passwd", cancellationToken: Ct);
        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
    }

    [Fact]
    public async Task Reading_a_missing_file_is_path_not_found()
    {
        using var dir = new TempDirectory();
        var act = () => Service.ReadAsync(dir.Path, "missing.txt", cancellationToken: Ct);
        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.PathNotFound);
        error.Hint.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Reading_a_folder_is_invalid_input()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.Combine("sub"));
        var act = () => Service.ReadAsync(dir.Path, "sub", cancellationToken: Ct);
        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
    }

    [Fact]
    public async Task Accepts_backslash_relative_paths()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("A backslash is an ordinary file-name character outside Windows.");
        }

        using var dir = new TempDirectory();
        dir.WriteFile("src/app/x.txt", "ok");

        var content = await Service.ReadAsync(dir.Path, @"src\app\x.txt", cancellationToken: Ct);

        content.Text.Should().Be("ok");
        content.RelativePath.Should().Be("src/app/x.txt");
    }
}
