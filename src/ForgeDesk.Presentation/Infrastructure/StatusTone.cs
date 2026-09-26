namespace ForgeDesk.Presentation.Infrastructure;

/// <summary>
/// Semantic color of a badge or status dot. The names match the app's status palette
/// (Neutral, Success, Warning, Danger, Info, Running); <see cref="None"/> shows nothing.
/// </summary>
public enum StatusTone
{
    None,
    Neutral,
    Success,
    Warning,
    Danger,
    Info,
    Running,
}
