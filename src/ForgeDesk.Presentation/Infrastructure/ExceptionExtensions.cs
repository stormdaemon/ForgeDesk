using ForgeDesk.Core.Common;

namespace ForgeDesk.Presentation.Infrastructure;

public static class ExceptionExtensions
{
    /// <summary>
    /// True for cancellations (closing a project, a newer request replacing an older one). They
    /// are never reported to the user, and must not escape a command: an async command rethrows
    /// on the UI thread.
    /// </summary>
    public static bool IsCancellation(this Exception exception) =>
        exception is OperationCanceledException or ForgeException { Kind: ErrorKind.Cancelled };
}
