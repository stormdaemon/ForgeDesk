using ForgeDesk.Core.GitHub;

namespace ForgeDesk.App.Controls;

/// <summary>Short labels and explanations for CI states.</summary>
internal static class CiText
{
    public static string Label(CiState state) => state switch
    {
        CiState.None => "No CI",
        CiState.Queued => "Queued",
        CiState.Running => "Running",
        CiState.Success => "Passing",
        CiState.Failure => "Failing",
        CiState.Cancelled => "Cancelled",
        _ => "Unknown",
    };

    public static string Description(CiState state) => state switch
    {
        CiState.None => "This repository has no GitHub Actions runs.",
        CiState.Queued => "The latest workflow run is waiting to start.",
        CiState.Running => "A workflow run is in progress.",
        CiState.Success => "The latest workflow runs passed.",
        CiState.Failure => "A recent workflow run failed.",
        CiState.Cancelled => "The latest workflow run was cancelled.",
        _ => "CI status is unavailable (offline or not signed in to GitHub).",
    };
}
