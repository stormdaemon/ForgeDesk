namespace ForgeDesk.App.Controls;

/// <summary>Resource keys of the Forge brushes for each <see cref="StatusKind"/>.</summary>
internal static class StatusResources
{
    /// <summary>Solid brush for dots, icons and status text.</summary>
    public static string BrushKey(StatusKind kind) => kind switch
    {
        StatusKind.Success => "ForgeSuccessBrush",
        StatusKind.Warning => "ForgeWarningBrush",
        StatusKind.Danger => "ForgeDangerBrush",
        StatusKind.Info => "ForgeInfoBrush",
        StatusKind.Running => "ForgeRunningBrush",
        _ => "ForgeNeutralBrush",
    };

    /// <summary>Translucent tint for pill and row backgrounds.</summary>
    public static string SubtleBrushKey(StatusKind kind) => kind switch
    {
        StatusKind.Success => "ForgeSuccessSubtleBrush",
        StatusKind.Warning => "ForgeWarningSubtleBrush",
        StatusKind.Danger => "ForgeDangerSubtleBrush",
        StatusKind.Info => "ForgeInfoSubtleBrush",
        StatusKind.Running => "ForgeAccentSubtleBrush",
        _ => "ForgeNeutralSubtleBrush",
    };

    /// <summary>Text on a subtle tint: the status color, or regular secondary text for neutral.</summary>
    public static string TextBrushKey(StatusKind kind) => kind switch
    {
        StatusKind.Neutral => "TextFillColorSecondaryBrush",
        StatusKind.Running => "ForgeAccentTextBrush",
        _ => BrushKey(kind),
    };
}
