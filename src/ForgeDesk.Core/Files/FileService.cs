using System.Text;
using ForgeDesk.Core.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeDesk.Core.Files;

/// <summary>Directory listings and file reads for the file tree and the file viewer.</summary>
internal sealed class FileService : IFileService
{
    private static readonly TimeSpan IgnoreCheckTimeout = TimeSpan.FromSeconds(10);

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tif", ".tiff",
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".pdb", ".so", ".dylib", ".o", ".obj", ".a", ".lib", ".class", ".jar", ".war", ".pyc", ".pyd",
        ".wasm", ".node", ".zip", ".7z", ".rar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".tar", ".nupkg", ".snupkg",
        ".msi", ".msix", ".appx", ".cab", ".iso", ".dmg", ".apk", ".aab", ".ipa", ".pdf", ".doc", ".docx", ".xls",
        ".xlsx", ".ppt", ".pptx", ".woff", ".woff2", ".ttf", ".otf", ".eot", ".mp3", ".mp4", ".m4a", ".wav", ".flac",
        ".ogg", ".avi", ".mov", ".mkv", ".webm", ".db", ".sqlite", ".sqlite3", ".mdb", ".pfx", ".p12", ".jks",
        ".keystore", ".psd", ".blend", ".fbx", ".lockb",
    };

    private readonly GitCli _git;
    private readonly ILogger<FileService> _logger;

    public FileService(GitCli git, ILogger<FileService>? logger = null)
    {
        _git = git;
        _logger = logger ?? NullLogger<FileService>.Instance;
    }

    public bool IsHeavyFolderName(string name) => HeavyFolders.IsHeavy(name);

    public async Task<IReadOnlyList<FileEntry>> ListDirectoryAsync(string projectRoot, string relativeDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var root = PathUtil.Normalize(projectRoot);
        var directory = string.IsNullOrWhiteSpace(relativeDirectory) || relativeDirectory is "." or "/"
            ? root
            : PathUtil.ResolveUnder(root, relativeDirectory);

        var entries = await Task.Run(() => Enumerate(root, directory, relativeDirectory), cancellationToken).ConfigureAwait(false);
        if (entries.Count == 0)
        {
            return entries;
        }

        var ignored = await FindIgnoredAsync(root, entries, cancellationToken).ConfigureAwait(false);
        if (ignored.Count > 0)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                if (ignored.Contains(entries[i].RelativePath))
                {
                    entries[i] = entries[i] with { IsIgnored = true };
                }
            }
        }

        return entries;
    }

    public async Task<FileContent> ReadAsync(string projectRoot, string relativePath, long maxTextBytes = 4 * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTextBytes, 1);

        var fullPath = PathUtil.ResolveUnder(projectRoot, relativePath);
        var displayPath = PathUtil.ToRelative(projectRoot, fullPath);

        // File-system probes are synchronous (and slow on network shares): keep them off the UI thread.
        return await Task.Run(() => ReadCoreAsync(fullPath, displayPath, maxTextBytes, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<FileContent> ReadCoreAsync(string fullPath, string displayPath, long maxTextBytes, CancellationToken cancellationToken)
    {
        if (Directory.Exists(fullPath))
        {
            throw new ForgeException(ErrorKind.InvalidInput, $"'{displayPath}' is a folder, not a file.");
        }

        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            throw new ForgeException(ErrorKind.PathNotFound, $"The file '{displayPath}' does not exist.",
                "It may have been moved or deleted. Refresh the file tree.");
        }

        var content = new FileContent
        {
            RelativePath = displayPath,
            Size = ContentLength(info),
            LastModified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
        };

        var extension = info.Extension;
        if (ImageExtensions.Contains(extension))
        {
            return content with { Kind = FileContentKind.Image };
        }

        if (BinaryExtensions.Contains(extension))
        {
            return content with { Kind = FileContentKind.Binary };
        }

        try
        {
            return await ReadTextAsync(fullPath, content, maxTextBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ForgeException(ErrorKind.PermissionDenied, $"ForgeDesk is not allowed to read '{displayPath}'.",
                "Check the file's permissions, or open it from an account that can read it.", ex.Message, ex);
        }
        catch (FileNotFoundException ex)
        {
            throw new ForgeException(ErrorKind.PathNotFound, $"The file '{displayPath}' does not exist.",
                "It may have been moved or deleted. Refresh the file tree.", ex.Message, ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new ForgeException(ErrorKind.PathNotFound, $"The file '{displayPath}' does not exist.",
                "It may have been moved or deleted. Refresh the file tree.", ex.Message, ex);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            throw new ForgeException(ErrorKind.PermissionDenied, $"'{displayPath}' is locked by another program.",
                "Close the program that is using it (a running build, an editor, an antivirus scan) and try again.", ex.Message, ex);
        }
        catch (IOException ex)
        {
            throw new ForgeException(ErrorKind.Unknown, $"'{displayPath}' could not be read.",
                "The disk or network share may be unavailable. Try again.", ex.Message, ex);
        }
    }

    private static async Task<FileContent> ReadTextAsync(string fullPath, FileContent content, long maxTextBytes, CancellationToken cancellationToken)
    {
        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            // Read one byte past the limit to know whether the file is truncated, whatever its reported size.
            var limit = (int)Math.Min(maxTextBytes, Array.MaxLength - 8);
            var capacity = (int)Math.Min(limit + 1L, Math.Max(stream.Length + 1, TextDecoding.BinarySniffLength));
            var buffer = new byte[capacity];
            var length = 0;
            var sniffed = false;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                length += read;
                if (!sniffed && length >= TextDecoding.BinarySniffLength)
                {
                    // Decide early so a large binary file is not read up to the text limit.
                    sniffed = true;
                    if (IsBinary(buffer.AsSpan(0, length)))
                    {
                        return content with { Kind = FileContentKind.Binary };
                    }
                }

                if (length == buffer.Length && length <= limit)
                {
                    // The file grew while reading: widen the buffer up to the limit.
                    Array.Resize(ref buffer, (int)Math.Min(limit + 1L, buffer.Length * 2L));
                }
            }

            var truncated = length > limit;
            ReadOnlySpan<byte> bytes = buffer.AsSpan(0, Math.Min(length, limit));
            if (!sniffed && IsBinary(bytes))
            {
                return content with { Kind = FileContentKind.Binary };
            }

            var bom = TextDecoding.DetectBom(bytes);

            string text;
            string encodingName;
            if (bom is { } detected)
            {
                var payload = bytes[detected.BomLength..];
                payload = payload[..(payload.Length - (payload.Length % detected.UnitSize))];
                text = detected.Encoding.GetString(payload);
                encodingName = detected.Name;
                if (truncated)
                {
                    text = CutAtLastLineBreak(text);
                }
            }
            else
            {
                if (truncated)
                {
                    // A '\n' byte never occurs inside a multi-byte UTF-8 sequence: cutting there keeps the text valid.
                    var lastNewline = bytes.LastIndexOf((byte)'\n');
                    if (lastNewline >= 0)
                    {
                        bytes = bytes[..(lastNewline + 1)];
                    }
                    else
                    {
                        bytes = TrimIncompleteUtf8(bytes);
                    }
                }

                (text, encodingName) = TextDecoding.DecodeWithoutBom(bytes);
            }

            return content with
            {
                Kind = FileContentKind.Text,
                Text = text,
                IsTruncated = truncated,
                EncodingName = encodingName,
                LineEndings = DetectLineEndings(text),
                LineCount = CountLines(text),
            };
        }
    }

    /// <summary>Size of what reading the file returns: for a symbolic link, the size of its target.</summary>
    private static long ContentLength(FileInfo info)
    {
        try
        {
            return info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is FileInfo { Exists: true } target
                ? target.Length
                : info.Length;
        }
        catch (IOException)
        {
            return info.Length;
        }
    }

    private static bool IsBinary(ReadOnlySpan<byte> bytes) => TextDecoding.DetectBom(bytes) is null && TextDecoding.LooksBinary(bytes);

    internal static string? DetectLineEndings(string text)
    {
        var crlf = 0;
        var lf = 0;
        var index = 0;
        while ((index = text.IndexOf('\n', index)) >= 0)
        {
            if (index > 0 && text[index - 1] == '\r')
            {
                crlf++;
            }
            else
            {
                lf++;
            }

            index++;
        }

        return (crlf, lf) switch
        {
            (0, 0) => null,
            (> 0, 0) => "CRLF",
            (0, > 0) => "LF",
            _ => "Mixed",
        };
    }

    internal static int CountLines(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        var count = text.AsSpan().Count('\n');
        return text[^1] == '\n' ? count : count + 1;
    }

    private static string CutAtLastLineBreak(string text)
    {
        var lastNewline = text.LastIndexOf('\n');
        return lastNewline >= 0 ? text[..(lastNewline + 1)] : text;
    }

    private static ReadOnlySpan<byte> TrimIncompleteUtf8(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.Length;
        var continuation = 0;
        while (end > 0 && continuation < 3 && (bytes[end - 1] & 0xC0) == 0x80)
        {
            end--;
            continuation++;
        }

        if (end > 0 && bytes[end - 1] >= 0xC0)
        {
            var lead = bytes[end - 1];
            var expected = lead >= 0xF0 ? 3 : lead >= 0xE0 ? 2 : 1;
            return continuation >= expected ? bytes : bytes[..(end - 1)];
        }

        return bytes;
    }

    private static bool IsSharingViolation(IOException ex)
    {
        // ERROR_SHARING_VIOLATION (32) and ERROR_LOCK_VIOLATION (33).
        var code = ex.HResult & 0xFFFF;
        return OperatingSystem.IsWindows() && code is 32 or 33;
    }

    private static List<FileEntry> Enumerate(string root, string directory, string relativeDirectory)
    {
        if (!Directory.Exists(directory))
        {
            if (File.Exists(directory))
            {
                throw new ForgeException(ErrorKind.InvalidInput, $"'{relativeDirectory}' is a file, not a folder.");
            }

            throw new ForgeException(ErrorKind.PathNotFound, $"The folder '{DisplayName(root, directory)}' does not exist.",
                "It may have been moved or deleted. Refresh the file tree.");
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };

        var entries = new List<FileEntry>();
        try
        {
            foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
            {
                if (TryCreateEntry(root, info) is { } entry)
                {
                    entries.Add(entry);
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ForgeException(ErrorKind.PermissionDenied, $"ForgeDesk is not allowed to open '{DisplayName(root, directory)}'.",
                "Check the folder's permissions, or run ForgeDesk from an account that can read it.", ex.Message, ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new ForgeException(ErrorKind.PathNotFound, $"The folder '{DisplayName(root, directory)}' does not exist.",
                "It may have been moved or deleted. Refresh the file tree.", ex.Message, ex);
        }

        entries.Sort(static (a, b) => a.IsDirectory != b.IsDirectory
            ? (a.IsDirectory ? -1 : 1)
            : NaturalStringComparer.Instance.Compare(a.Name, b.Name));
        return entries;
    }

    private static FileEntry? TryCreateEntry(string root, FileSystemInfo info)
    {
        try
        {
            var attributes = info.Attributes;
            var isDirectory = (attributes & FileAttributes.Directory) != 0;

            // ReparsePoint alone also marks cloud placeholders (OneDrive): only real links count, and they are never followed.
            var isLink = (attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget is not null;
            return new FileEntry
            {
                Name = info.Name,
                RelativePath = PathUtil.ToRelative(root, info.FullName),
                IsDirectory = isDirectory,
                Size = !isDirectory && info is FileInfo file && !isLink ? SafeLength(file) : 0,
                LastModified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                IsHidden = info.Name.StartsWith('.') || (attributes & FileAttributes.Hidden) != 0,
                IsHeavyFolder = isDirectory && HeavyFolders.IsHeavy(info.Name),
                IsSymbolicLink = isLink,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The entry vanished or cannot be inspected between enumeration and stat.
            return null;
        }
    }

    private static long SafeLength(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static string DisplayName(string root, string directory)
    {
        var relative = PathUtil.ToRelative(root, directory);
        return relative == "." ? Path.GetFileName(root) : relative;
    }

    /// <summary>Asks git which of the listed entries are ignored; empty when git or the repository is unavailable.</summary>
    private async Task<HashSet<string>> FindIgnoredAsync(string root, List<FileEntry> entries, CancellationToken cancellationToken)
    {
        var ignored = new HashSet<string>(StringComparer.Ordinal);
        if (!GitCli.IsInsideRepository(root))
        {
            return ignored;
        }

        var input = new StringBuilder();
        foreach (var entry in entries)
        {
            input.Append(entry.RelativePath).Append('\0');
        }

        try
        {
            var result = await _git.TryRunAsync(root, ["check-ignore", "-z", "--stdin"], cancellationToken, input.ToString(), IgnoreCheckTimeout)
                .ConfigureAwait(false);

            // Exit code 1 means "nothing ignored"; 128 means not a repository or git failed.
            if (result is { ExitCode: 0 })
            {
                foreach (var path in GitCli.SplitNul(result.StandardOutput))
                {
                    ignored.Add(path.TrimEnd('/'));
                }
            }
            else if (result is { ExitCode: not 1 })
            {
                _logger.LogDebug("git check-ignore failed in {Root}: {Error}", root, result.StandardError);
            }
        }
        catch (ForgeException ex)
        {
            _logger.LogDebug(ex, "git check-ignore could not run in {Root}", root);
        }

        return ignored;
    }
}
