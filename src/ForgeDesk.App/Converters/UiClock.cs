using System.ComponentModel;
using System.Windows.Threading;

namespace ForgeDesk.App.Converters;

/// <summary>
/// A shared clock that ticks every 30 seconds on the UI thread. Bind to <see cref="Now"/> as a
/// second value of a <see cref="RelativeTimeConverter"/> multi-binding to keep "3 min ago" fresh:
/// <c>&lt;Binding Source="{x:Static conv:UiClock.Instance}" Path="Now" /&gt;</c>.
/// </summary>
public sealed class UiClock : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs NowChanged = new(nameof(Now));
    private readonly DispatcherTimer _timer;

    private UiClock()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) =>
        {
            Now = DateTimeOffset.Now;
            PropertyChanged?.Invoke(this, NowChanged);
        };
        _timer.Start();
    }

    /// <summary>The UI-thread clock (created on first use, which must happen on the UI thread).</summary>
    public static UiClock Instance { get; } = new();

    public DateTimeOffset Now { get; private set; } = DateTimeOffset.Now;

    public event PropertyChangedEventHandler? PropertyChanged;
}
