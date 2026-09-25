using CommunityToolkit.Mvvm.ComponentModel;
using ForgeDesk.Core.Common;

namespace ForgeDesk.Presentation.Infrastructure;

/// <summary>
/// Base class for every view model: busy/error state and a single guarded way to run
/// asynchronous work so failures are always surfaced (never swallowed silently).
/// </summary>
public abstract partial class ViewModelBase : ObservableObject
{
    private int _busyCount;

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string? BusyMessage { get; private set; }

    /// <summary>Blocking error for the view (rendered by an error panel with a retry action).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial ErrorInfo? Error { get; set; }

    public bool HasError => Error is not null;

    /// <summary>
    /// Runs <paramref name="work"/> while flagging the view model busy. Exceptions are converted
    /// to <see cref="Error"/> (when <paramref name="errorMode"/> is Inline) or sent to
    /// <paramref name="notifications"/> (Toast). Cancellation is silent. Returns true on success.
    /// </summary>
    protected async Task<bool> RunAsync(
        Func<Task> work,
        string? busyMessage = null,
        string? errorTitle = null,
        ErrorMode errorMode = ErrorMode.Inline,
        INotificationService? notifications = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        EnterBusy(busyMessage);
        try
        {
            if (errorMode == ErrorMode.Inline)
            {
                Error = null;
            }

            await work();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ForgeException ex) when (ex.Kind == ErrorKind.Cancelled)
        {
            return false;
        }
        catch (Exception ex)
        {
            var info = ErrorInfo.From(ex, errorTitle);
            if (errorMode == ErrorMode.Toast && notifications is not null)
            {
                notifications.ShowError(info);
            }
            else
            {
                Error = info;
            }

            return false;
        }
        finally
        {
            ExitBusy();
        }
    }

    private void EnterBusy(string? message)
    {
        _busyCount++;
        BusyMessage = message;
        IsBusy = true;
    }

    private void ExitBusy()
    {
        _busyCount = Math.Max(0, _busyCount - 1);
        if (_busyCount == 0)
        {
            IsBusy = false;
            BusyMessage = null;
        }
    }
}

public enum ErrorMode
{
    /// <summary>Show in the view's error panel.</summary>
    Inline,

    /// <summary>Show as a transient notification (for actions like push/stage).</summary>
    Toast,
}
