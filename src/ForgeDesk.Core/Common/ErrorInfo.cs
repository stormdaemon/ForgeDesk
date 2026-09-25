namespace ForgeDesk.Core.Common;

/// <summary>Immutable, UI-friendly snapshot of an error.</summary>
public sealed record ErrorInfo(ErrorKind Kind, string Title, string Message, string? Hint = null, string? Detail = null)
{
    public static ErrorInfo From(Exception exception, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            ForgeException fe => new ErrorInfo(fe.Kind, title ?? DefaultTitle(fe.Kind), fe.Message, fe.Hint, fe.Detail),
            OperationCanceledException => new ErrorInfo(ErrorKind.Cancelled, title ?? "Cancelled", "The operation was cancelled."),
            UnauthorizedAccessException => new ErrorInfo(ErrorKind.PermissionDenied, title ?? "Access denied", exception.Message,
                "Check that you have permission to access this location, or run the operation on a folder you own."),
            DirectoryNotFoundException or FileNotFoundException => new ErrorInfo(ErrorKind.PathNotFound, title ?? "Not found", exception.Message),
            IOException => new ErrorInfo(ErrorKind.Unknown, title ?? "File system error", exception.Message,
                "The file may be in use by another program. Close it and try again."),
            TimeoutException => new ErrorInfo(ErrorKind.Timeout, title ?? "Timed out", exception.Message),
            _ => new ErrorInfo(ErrorKind.Unknown, title ?? "Something went wrong", exception.Message, null, exception.ToString()),
        };
    }

    public static string DefaultTitle(ErrorKind kind) => kind switch
    {
        ErrorKind.InvalidInput => "Invalid input",
        ErrorKind.PathNotFound => "Location not found",
        ErrorKind.PermissionDenied => "Access denied",
        ErrorKind.FileTooLarge => "File too large",
        ErrorKind.AlreadyExists => "Already exists",
        ErrorKind.Cancelled => "Cancelled",
        ErrorKind.Timeout => "Timed out",
        ErrorKind.ToolNotFound => "Tool not found",
        ErrorKind.ProcessFailed => "Command failed",
        ErrorKind.GitNotFound => "Git is not installed",
        ErrorKind.NotARepository => "Not a Git repository",
        ErrorKind.GitCommandFailed => "Git operation failed",
        ErrorKind.MergeConflict => "Merge conflict",
        ErrorKind.NonFastForward => "Remote has new changes",
        ErrorKind.DirtyWorkingTree => "Uncommitted changes",
        ErrorKind.NoUpstream => "No upstream branch",
        ErrorKind.NothingToCommit => "Nothing to commit",
        ErrorKind.RepositoryLocked => "Repository is locked",
        ErrorKind.DetachedHead => "Detached HEAD",
        ErrorKind.NetworkUnavailable => "Network unavailable",
        ErrorKind.AuthenticationRequired => "Sign-in required",
        ErrorKind.AuthenticationFailed => "Authentication failed",
        ErrorKind.RateLimited => "Rate limit reached",
        ErrorKind.NotFound => "Not found",
        ErrorKind.RemoteRejected => "Remote rejected the operation",
        ErrorKind.StorageFailure => "Storage error",
        ErrorKind.StorageCorrupted => "Local data was damaged",
        _ => "Something went wrong",
    };
}
