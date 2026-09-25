namespace ForgeDesk.Core.Common;

/// <summary>
/// The single exception type services throw for expected failures. It carries a
/// user-readable message, an optional recovery hint and optional raw technical
/// detail (e.g. git stderr) that the UI can show in an expandable section.
/// </summary>
public class ForgeException : Exception
{
    public ForgeException(ErrorKind kind, string message, string? hint = null, string? detail = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        Hint = hint;
        Detail = detail;
    }

    public ErrorKind Kind { get; }

    /// <summary>What the user can do about it, phrased as an instruction.</summary>
    public string? Hint { get; }

    /// <summary>Raw technical output (stderr, HTTP body…) for the "details" expander.</summary>
    public string? Detail { get; }

    public static ForgeException Cancelled() => new(ErrorKind.Cancelled, "The operation was cancelled.");

    public static ForgeException NotFound(string what) => new(ErrorKind.NotFound, $"{what} was not found.");

    public static ForgeException InvalidInput(string message) => new(ErrorKind.InvalidInput, message);
}
