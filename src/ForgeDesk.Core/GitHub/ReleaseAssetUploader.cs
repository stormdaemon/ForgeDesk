using System.Net.Http.Headers;
using ForgeDesk.Core.Common;
using Octokit;

namespace ForgeDesk.Core.GitHub;

/// <summary>
/// Streams a file to a release's upload URL with byte-level progress. GitHub rejects assets of
/// 2 GiB or more; the timeout scales with the size (large installers on slow links).
/// </summary>
internal sealed class ReleaseAssetUploader
{
    public const long MaxAssetBytes = (2L * 1024 * 1024 * 1024) - 1;

    private const int BufferSize = 81920;
    private const string ApiVersion = "2022-11-28";

    // Assumed worst-case throughput when computing the timeout: 64 KiB/s on top of a fixed allowance.
    private const long MinimumBytesPerSecond = 64 * 1024;
    private static readonly TimeSpan BaseTimeout = TimeSpan.FromMinutes(10);

    private readonly GitHubClientFactory _factory;

    public ReleaseAssetUploader(GitHubClientFactory factory)
    {
        _factory = factory;
    }

    public static TimeSpan TimeoutFor(long bytes) => BaseTimeout + TimeSpan.FromSeconds(Math.Max(0, bytes) / (double)MinimumBytesPerSecond);

    /// <param name="uploadUrlTemplate">The release's <c>upload_url</c> ("https://uploads.github.com/repos/o/r/releases/1/assets{?name,label}").</param>
    public async Task<GitHubReleaseAsset> UploadAsync(string uploadUrlTemplate, string token, string filePath, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadUrlTemplate);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var file = ValidateFile(filePath);
        var fileName = file.Name;

        await using var source = new FileStream(file.FullName, System.IO.FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var body = new ProgressReportingStream(source, file.Length, progress);
        using var content = new StreamContent(body, BufferSize);
        content.Headers.ContentType = new MediaTypeHeaderValue(ReleaseAssetContentTypes.FromFileName(fileName));
        content.Headers.ContentLength = file.Length;

        using var request = new HttpRequestMessage(HttpMethod.Post, ExpandUploadUrl(uploadUrlTemplate, fileName)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(GitHubClientOptions.ProductName, _factory.ProductVersion));
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);

        using var timeout = new CancellationTokenSource(TimeoutFor(file.Length));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        HttpResponseMessage response;
        try
        {
            response = await _factory.UploadClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ForgeException(ErrorKind.Timeout, $"Uploading '{fileName}' took too long and was stopped.",
                "Check your connection and try again.", ex.Message, ex);
        }

        using (response)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var headers = response.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
                throw GitHubErrorTranslator.FromResponse(response.StatusCode, responseBody, headers, "The release", _factory.Serializer);
            }

            var asset = _factory.Serializer.Deserialize<ReleaseAsset>(responseBody)
                ?? throw new ForgeException(ErrorKind.RemoteRejected, "GitHub accepted the upload but returned an unexpected response.", null, responseBody);
            return GitHubMapper.ToAsset(asset);
        }
    }

    /// <summary>Expands GitHub's RFC 6570 template ("…/assets{?name,label}") with the asset name.</summary>
    internal static Uri ExpandUploadUrl(string template, string fileName)
    {
        var brace = template.IndexOf('{', StringComparison.Ordinal);
        var baseUrl = brace >= 0 ? template[..brace] : template;
        return new Uri($"{baseUrl}?name={Uri.EscapeDataString(fileName)}", UriKind.Absolute);
    }

    /// <summary>Checks the file before any network call: it must exist and fit GitHub's size limit.</summary>
    internal static FileInfo ValidateFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var file = new FileInfo(filePath);
        if (!file.Exists)
        {
            throw new ForgeException(ErrorKind.PathNotFound, $"The file '{file.Name}' doesn't exist.",
                "It may have been moved or deleted. Build it again or pick another file.", file.FullName);
        }

        if (file.Length > MaxAssetBytes)
        {
            throw new ForgeException(ErrorKind.FileTooLarge,
                $"'{file.Name}' is {PathUtil.FormatBytes(file.Length)}; GitHub release files must be smaller than 2 GB.",
                "Split the file or compress it before attaching it to the release.");
        }

        return file;
    }
}
