namespace ForgeDesk.Core.GitHub;

/// <summary>Media types sent with release assets (GitHub stores and serves them as given).</summary>
internal static class ReleaseAssetContentTypes
{
    public const string Default = "application/octet-stream";

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".zip"] = "application/zip",
        [".7z"] = "application/x-7z-compressed",
        [".gz"] = "application/gzip",
        [".tgz"] = "application/gzip",
        [".tar"] = "application/x-tar",
        [".bz2"] = "application/x-bzip2",
        [".xz"] = "application/x-xz",
        [".zst"] = "application/zstd",
        [".exe"] = "application/vnd.microsoft.portable-executable",
        [".dll"] = "application/vnd.microsoft.portable-executable",
        [".msi"] = "application/x-msi",
        [".msix"] = "application/msix",
        [".msixbundle"] = "application/msixbundle",
        [".appx"] = "application/appx",
        [".appxbundle"] = "application/appxbundle",
        [".nupkg"] = "application/zip",
        [".snupkg"] = "application/zip",
        [".vsix"] = "application/zip",
        [".jar"] = "application/java-archive",
        [".apk"] = "application/vnd.android.package-archive",
        [".deb"] = "application/vnd.debian.binary-package",
        [".rpm"] = "application/x-rpm",
        [".dmg"] = "application/x-apple-diskimage",
        [".pkg"] = "application/octet-stream",
        [".wasm"] = "application/wasm",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".yml"] = "application/yaml",
        [".yaml"] = "application/yaml",
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
        [".csv"] = "text/csv",
        [".html"] = "text/html",
        [".sha256"] = "text/plain",
        [".sha512"] = "text/plain",
        [".sig"] = "application/pgp-signature",
        [".asc"] = "application/pgp-signature",
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/vnd.microsoft.icon",
    };

    public static string FromFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return !string.IsNullOrEmpty(extension) && ByExtension.TryGetValue(extension, out var type) ? type : Default;
    }
}
