namespace ForgeDesk.Core.Common;

/// <summary>Helper to raise events without letting a faulty subscriber break the publisher.</summary>
public static class SafeEvent
{
    public static void Raise<T>(EventHandler<T>? handler, object sender, T args)
    {
        if (handler is null)
        {
            return;
        }

        foreach (var single in handler.GetInvocationList().Cast<EventHandler<T>>())
        {
            try
            {
                single(sender, args);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"Event subscriber failed: {ex}");
            }
        }
    }
}
