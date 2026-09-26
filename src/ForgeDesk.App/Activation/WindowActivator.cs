using System.Windows;
using System.Windows.Interop;
using ForgeDesk.App.Native;

namespace ForgeDesk.App.Activation;

/// <summary>Restores and brings a window to the foreground.</summary>
internal static class WindowActivator
{
    public static void BringToFront(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!window.IsVisible)
        {
            window.Show();
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (window.WindowState == WindowState.Minimized)
        {
            // SW_RESTORE returns to the pre-minimize state, including maximized.
            if (handle != IntPtr.Zero)
            {
                NativeMethods.ShowWindow(handle, NativeMethods.SwRestore);
            }
            else
            {
                window.WindowState = WindowState.Normal;
            }
        }

        window.Activate();

        // Windows restricts focus stealing; a Topmost toggle reliably lifts the window, and the
        // second instance already granted us the right to take the foreground.
        window.Topmost = true;
        window.Topmost = false;
        if (handle != IntPtr.Zero)
        {
            NativeMethods.SetForegroundWindow(handle);
        }

        window.Focus();
    }
}
