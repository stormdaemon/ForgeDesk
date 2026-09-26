using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeDesk.Core.Projects;

/// <summary>Clones a repository into a new (or empty) folder and registers it as a project.</summary>
internal sealed class ProjectCloneService : IProjectCloneService
{
    private readonly IGitService _git;
    private readonly IProjectRegistry _registry;
    private readonly IActivityLog _activity;
    private readonly IClock _clock;
    private readonly ILogger<ProjectCloneService> _logger;

    public ProjectCloneService(IGitService git, IProjectRegistry registry, IActivityLog activity, IClock clock, ILogger<ProjectCloneService>? logger = null)
    {
        _git = git;
        _registry = registry;
        _activity = activity;
        _clock = clock;
        _logger = logger ?? NullLogger<ProjectCloneService>.Instance;
    }

    public async Task<Project> CloneAsync(string remoteUrl, string targetDirectory, IProgress<GitProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var url = CloneUrl.Validate(remoteUrl);
        var target = PrepareTarget(targetDirectory);
        if (await _registry.FindByPathAsync(target.Path, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            throw new ForgeException(ErrorKind.AlreadyExists, $"The folder '{target.Path}' is already used by the project '{existing.Name}'.",
                "Choose another folder for the clone.");
        }

        try
        {
            await _git.CloneAsync(url, target.Path, progress, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A failed or cancelled clone must not leave a half-written repository behind.
            RemoveIncompleteClone(target);
            throw;
        }

        Project project;
        try
        {
            project = await _registry.AddAsync(target.Path, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The clone itself succeeded: keep the files, the user can add the folder later.
            var inner = ex as ForgeException;
            throw new ForgeException(inner?.Kind ?? ErrorKind.Unknown,
                $"The repository was cloned to '{target.Path}', but it could not be added to your projects: {ex.Message}",
                "The files are there. Use \"Add project\" to add the folder once the problem is solved.",
                inner?.Detail ?? ex.ToString(), ex);
        }

        await ActivityRecorder.TryRecordAsync(_activity, new ActivityEntry
        {
            ProjectId = project.Id,
            At = _clock.Now,
            Kind = ActivityKind.ProjectCloned,
            Outcome = ActivityOutcome.Success,
            Title = $"Cloned {project.GitHub?.FullName ?? project.Name}",
            Detail = CloneUrl.Redact(url),
            RefKind = "url",
            RefValue = project.GitHub?.HtmlUrl,
        }, _logger).ConfigureAwait(false);

        return project;
    }

    private static CloneTarget PrepareTarget(string? targetDirectory)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new ForgeException(ErrorKind.InvalidInput, "Choose the folder to clone into.");
        }

        var path = ProjectFolder.NormalizeOrThrow(targetDirectory);
        if (ProjectFolder.IsDriveRoot(path))
        {
            throw new ForgeException(ErrorKind.InvalidInput, "A repository cannot be cloned into the root of a drive.",
                "Choose or create a folder for it, for example in your projects folder.");
        }

        if (File.Exists(path))
        {
            throw new ForgeException(ErrorKind.AlreadyExists, $"A file named '{Path.GetFileName(path)}' already exists in '{Path.GetDirectoryName(path)}'.",
                "Choose another folder name for the clone.");
        }

        var existed = Directory.Exists(path);
        if (existed)
        {
            bool isEmpty;
            try
            {
                isEmpty = !Directory.EnumerateFileSystemEntries(path).Any();
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new ForgeException(ErrorKind.PermissionDenied, $"ForgeDesk cannot read the folder '{path}'.",
                    "Choose a folder you have access to.", ex.Message, ex);
            }

            if (!isEmpty)
            {
                throw new ForgeException(ErrorKind.AlreadyExists, $"The folder '{path}' already exists and is not empty.",
                    "Choose an empty folder or a new folder name.");
            }
        }
        else
        {
            var parent = Path.GetDirectoryName(path)!;
            try
            {
                Directory.CreateDirectory(parent);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new ForgeException(ErrorKind.PermissionDenied, $"ForgeDesk cannot create the folder '{parent}'.",
                    "Choose a location you have write access to.", ex.Message, ex);
            }
            catch (IOException ex)
            {
                throw new ForgeException(ErrorKind.PathNotFound, $"The folder '{parent}' could not be created.",
                    "Check that the drive is available and the path is valid.", ex.Message, ex);
            }
        }

        return new CloneTarget(path, existed);
    }

    private void RemoveIncompleteClone(CloneTarget target)
    {
        try
        {
            if (!Directory.Exists(target.Path))
            {
                return;
            }

            if (target.ExistedBefore)
            {
                // The folder was empty before: empty it again but keep it.
                foreach (var entry in new DirectoryInfo(target.Path).EnumerateFileSystemInfos())
                {
                    SafeDelete.Tree(entry);
                }
            }
            else
            {
                SafeDelete.Tree(new DirectoryInfo(target.Path));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove the incomplete clone in {Path}", target.Path);
        }
    }

    private sealed record CloneTarget(string Path, bool ExistedBefore);
}
