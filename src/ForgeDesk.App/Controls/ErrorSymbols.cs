using ForgeDesk.Core.Common;
using Wpf.Ui.Controls;

namespace ForgeDesk.App.Controls;

/// <summary>The Fluent icon that best explains an <see cref="ErrorKind"/>.</summary>
internal static class ErrorSymbols
{
    public static SymbolRegular For(ErrorKind kind) => kind switch
    {
        ErrorKind.NetworkUnavailable => SymbolRegular.WifiOff24,
        ErrorKind.AuthenticationRequired or ErrorKind.AuthenticationFailed => SymbolRegular.LockClosed24,
        ErrorKind.PermissionDenied => SymbolRegular.ShieldError24,
        ErrorKind.PathNotFound => SymbolRegular.FolderProhibited24,
        ErrorKind.NotFound => SymbolRegular.DocumentDismiss24,
        ErrorKind.FileTooLarge => SymbolRegular.DocumentError24,
        ErrorKind.StorageFailure or ErrorKind.StorageCorrupted => SymbolRegular.Database24,
        ErrorKind.Timeout => SymbolRegular.Timer24,
        ErrorKind.RateLimited => SymbolRegular.Clock24,
        ErrorKind.ToolNotFound or ErrorKind.GitNotFound => SymbolRegular.Wrench24,
        ErrorKind.NotARepository or ErrorKind.DetachedHead or ErrorKind.NoUpstream => SymbolRegular.Branch24,
        ErrorKind.MergeConflict or ErrorKind.NonFastForward or ErrorKind.RemoteRejected => SymbolRegular.BranchFork24,
        ErrorKind.DirtyWorkingTree or ErrorKind.NothingToCommit => SymbolRegular.DocumentText24,
        ErrorKind.RepositoryLocked => SymbolRegular.LockClosed24,
        ErrorKind.Cancelled => SymbolRegular.Prohibited24,
        ErrorKind.InvalidInput => SymbolRegular.Warning24,
        _ => SymbolRegular.ErrorCircle24,
    };
}
