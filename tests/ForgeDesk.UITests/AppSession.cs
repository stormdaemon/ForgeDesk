using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace ForgeDesk.UITests;

/// <summary>
/// Launches the published ForgeDesk.exe with an isolated data folder, gives access to its main
/// window and writes screenshots (and the app's logs) to FORGEDESK_SCREENSHOTS for review.
/// </summary>
public sealed class AppSession : IDisposable
{
    private readonly UIA3Automation _automation = new();
    private readonly Application _app;

    private AppSession(Application app, string dataDirectory)
    {
        _app = app;
        DataDirectory = dataDirectory;
    }

    public static string? AppPath => Environment.GetEnvironmentVariable("FORGEDESK_APP_PATH");

    public static string ScreenshotsDirectory =>
        Environment.GetEnvironmentVariable("FORGEDESK_SCREENSHOTS") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Path.GetTempPath(), "forgedesk-screenshots");

    public string DataDirectory { get; }

    public Window MainWindow { get; private set; } = null!;

    public Process Process => Process.GetProcessById(_app.ProcessId);

    public bool HasExited => _app.HasExited;

    public static AppSession Launch(string dataDirectory, params string[] arguments)
    {
        var path = AppPath ?? throw new InvalidOperationException("FORGEDESK_APP_PATH is not set.");
        var psi = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        psi.Environment["FORGEDESK_DATA_DIR"] = dataDirectory;
        var app = Application.Launch(psi);
        var session = new AppSession(app, dataDirectory);
        session.MainWindow = app.GetMainWindow(session._automation, TimeSpan.FromSeconds(90))
            ?? throw new InvalidOperationException("ForgeDesk's main window did not appear.");
        session.MainWindow.SetForeground();
        return session;
    }

    /// <summary>Lets the UI settle (async loads, animations) before a screenshot.</summary>
    public static void Settle(double seconds = 2.5) => Thread.Sleep(TimeSpan.FromSeconds(seconds));

    public string Screenshot(string name)
    {
        Directory.CreateDirectory(ScreenshotsDirectory);
        var file = Path.Combine(ScreenshotsDirectory, name + ".png");
        MainWindow.SetForeground();
        Capture.Element(MainWindow).ToFile(file);
        if (Environment.GetEnvironmentVariable("FORGEDESK_DUMP_UIA") == "1")
        {
            DumpTree(name);
        }

        return file;
    }

    /// <summary>Writes the UI Automation tree (type, name, id, bounds) next to the screenshot for diagnosis.</summary>
    public void DumpTree(string name)
    {
        Directory.CreateDirectory(ScreenshotsDirectory);
        var builder = new System.Text.StringBuilder();
        var count = 0;
        void Walk(AutomationElement element, int depth)
        {
            if (count++ > 6000 || depth > 40)
            {
                return;
            }

            try
            {
                var bounds = element.BoundingRectangle;
                builder.Append(' ', depth * 2)
                    .Append(element.ControlType).Append(" \"").Append(element.Properties.Name.ValueOrDefault).Append('"')
                    .Append(element.Properties.AutomationId.ValueOrDefault is { Length: > 0 } id ? $" #{id}" : string.Empty)
                    .Append($" [{bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}]")
                    .Append(element.Properties.IsOffscreen.ValueOrDefault ? " offscreen" : string.Empty)
                    .Append(element.Properties.IsEnabled.ValueOrDefault ? string.Empty : " disabled")
                    .AppendLine();
                foreach (var child in element.FindAllChildren())
                {
                    Walk(child, depth + 1);
                }
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TimeoutException or InvalidOperationException)
            {
                builder.Append(' ', depth * 2).AppendLine("<unavailable>");
            }
        }

        Walk(MainWindow, 0);
        File.WriteAllText(Path.Combine(ScreenshotsDirectory, name + ".uia.txt"), builder.ToString());
    }

    public void Press(params VirtualKeyShort[] keys)
    {
        MainWindow.SetForeground();
        Keyboard.TypeSimultaneously(keys);
        Thread.Sleep(300);
    }

    public void Type(string text)
    {
        Keyboard.Type(text);
        Thread.Sleep(300);
    }

    public AutomationElement? FindByName(string name, TimeSpan? timeout = null) =>
        Retry(() => MainWindow.FindFirstDescendant(cf => cf.ByName(name)), timeout ?? TimeSpan.FromSeconds(10));

    public AutomationElement? FindById(string automationId, TimeSpan? timeout = null) =>
        Retry(() => MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId)), timeout ?? TimeSpan.FromSeconds(10));

    private static AutomationElement? Retry(Func<AutomationElement?> find, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        do
        {
            try
            {
                if (find() is { } found)
                {
                    return found;
                }
            }
            catch (Exception ex) when (ex is TimeoutException or System.Runtime.InteropServices.COMException)
            {
            }

            Thread.Sleep(250);
        }
        while (sw.Elapsed < timeout);
        return null;
    }

    public void Dispose()
    {
        try
        {
            if (!_app.HasExited)
            {
                _app.Close();
                if (!_app.WaitWhileMainHandleIsMissing(TimeSpan.FromSeconds(1)) && !_app.HasExited)
                {
                    Thread.Sleep(2000);
                }

                if (!_app.HasExited)
                {
                    _app.Kill();
                }
            }
        }
        catch (Exception)
        {
            // Best effort: the process may already be gone.
        }

        _app.Dispose();
        _automation.Dispose();
        CopyLogs();
    }

    private void CopyLogs()
    {
        try
        {
            var logs = Path.Combine(DataDirectory, "logs");
            if (!Directory.Exists(logs))
            {
                return;
            }

            var target = Path.Combine(ScreenshotsDirectory, "logs", Path.GetFileName(DataDirectory));
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(logs))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
