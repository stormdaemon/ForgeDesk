using System.Windows;
using ForgeDesk.Presentation.Terminal;

namespace ForgeDesk.App.Features.Terminal;

/// <summary>
/// App-wide cache of the live terminals, one <see cref="TerminalSessionHost"/> per session. Views
/// come and go (switching projects rebuilds the workspace page) but the terminal controls and
/// their processes stay here until the session is closed — by the user, or with its project.
/// </summary>
internal static class TerminalHostRegistry
{
    private static readonly Dictionary<TerminalSessionViewModel, TerminalSessionHost> Hosts = new(ReferenceEqualityComparer.Instance);

    /// <summary>The host of a session, created (and attached, which starts the shell) on first use. Null once the session is closed.</summary>
    public static TerminalSessionHost? GetOrCreate(TerminalSessionViewModel session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (Hosts.TryGetValue(session, out var host))
        {
            return host;
        }

        if (session.IsClosed || Application.Current?.Dispatcher is not { } dispatcher)
        {
            return null;
        }

        session.Closed += OnSessionClosed;
        host = new TerminalSessionHost(session, dispatcher);
        Hosts[session] = host;
        return host;
    }

    public static TerminalSessionHost? Find(TerminalSessionViewModel? session) =>
        session is not null && Hosts.TryGetValue(session, out var host) ? host : null;

    private static void OnSessionClosed(object? sender, EventArgs e)
    {
        if (sender is not TerminalSessionViewModel session)
        {
            return;
        }

        session.Closed -= OnSessionClosed;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => Release(session));
        }
        else
        {
            Release(session);
        }
    }

    private static void Release(TerminalSessionViewModel session)
    {
        if (Hosts.Remove(session, out var host))
        {
            host.Dispose();
        }
    }
}
