using ForgeDesk.Core.Common;

namespace ForgeDesk.Presentation.Infrastructure;

public sealed record ConfirmOptions
{
    public required string Title { get; init; }
    public required string Message { get; init; }
    public string ConfirmText { get; init; } = "OK";
    public string CancelText { get; init; } = "Cancel";

    /// <summary>Styles the confirm button as dangerous (delete, discard, force push…).</summary>
    public bool IsDestructive { get; init; }

    /// <summary>Optional checkbox ("Also delete remote branch"); its value is returned in the result.</summary>
    public string? CheckboxText { get; init; }
    public bool CheckboxDefault { get; init; }
}

public sealed record ConfirmResult(bool Confirmed, bool CheckboxChecked);

public sealed record PromptOptions
{
    public required string Title { get; init; }
    public string? Message { get; init; }
    public string? InitialValue { get; init; }
    public string? Placeholder { get; init; }
    public string ConfirmText { get; init; } = "OK";
    public bool Multiline { get; init; }

    /// <summary>Returns an error message for invalid input, or null when valid.</summary>
    public Func<string, string?>? Validate { get; init; }
}

/// <summary>A view model shown in its own modal window (resolved to a view by DataTemplate).</summary>
public interface IDialogViewModel
{
    string Title { get; }

    double PreferredWidth => 560;

    double? PreferredHeight => null;

    /// <summary>Raised by the view model to close its window with a result.</summary>
    event EventHandler<bool?>? CloseRequested;
}

public interface IDialogService
{
    Task<bool> ConfirmAsync(ConfirmOptions options);

    Task<ConfirmResult> ConfirmWithCheckboxAsync(ConfirmOptions options);

    Task<string?> PromptAsync(PromptOptions options);

    Task<string?> PickFolderAsync(string title, string? initialDirectory = null);

    Task<IReadOnlyList<string>> PickFilesAsync(string title, string? initialDirectory = null, bool allowMultiple = true);

    Task ShowErrorAsync(ErrorInfo error);

    /// <summary>Shows a custom dialog; returns the result passed to CloseRequested.</summary>
    Task<bool?> ShowDialogAsync(IDialogViewModel dialog);
}
