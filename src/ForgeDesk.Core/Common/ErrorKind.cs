namespace ForgeDesk.Core.Common;

/// <summary>
/// Stable, user-facing categories of failure. The presentation layer uses these
/// to pick an icon, a title and recovery actions; never parse messages instead.
/// </summary>
public enum ErrorKind
{
    Unknown = 0,

    // Input / environment
    InvalidInput,
    PathNotFound,
    PermissionDenied,
    FileTooLarge,
    AlreadyExists,
    Cancelled,
    Timeout,

    // Processes
    ToolNotFound,
    ProcessFailed,

    // Git
    GitNotFound,
    NotARepository,
    GitCommandFailed,
    MergeConflict,
    NonFastForward,
    DirtyWorkingTree,
    NoUpstream,
    NothingToCommit,
    RepositoryLocked,
    DetachedHead,

    // Network / GitHub
    NetworkUnavailable,
    AuthenticationRequired,
    AuthenticationFailed,
    RateLimited,
    NotFound,
    RemoteRejected,

    // Storage
    StorageFailure,
    StorageCorrupted,
}
