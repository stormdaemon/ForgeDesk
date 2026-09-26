namespace ForgeDesk.Presentation.Shell;

/// <summary>
/// Runs the "Clone repository" flow (repository picker, destination, progress). Provided by the
/// feature that owns cloning; while none is registered, the shell disables every clone entry point.
/// </summary>
public interface ICloneRequestHandler
{
    Task RequestCloneAsync();
}
