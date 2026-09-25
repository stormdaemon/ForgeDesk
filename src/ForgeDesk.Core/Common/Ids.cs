namespace ForgeDesk.Core.Common;

public static class Ids
{
    /// <summary>Compact, sortable-enough unique id (32 lowercase hex chars, v7 GUID).</summary>
    public static string New() => Guid.CreateVersion7().ToString("N");
}
