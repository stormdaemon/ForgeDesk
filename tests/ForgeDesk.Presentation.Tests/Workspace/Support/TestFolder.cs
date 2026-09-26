namespace ForgeDesk.Presentation.Tests.Workspace.Support;

/// <summary>A temporary directory deleted on dispose (project roots must exist for FolderExists).</summary>
public sealed class TestFolder : IDisposable
{
    public TestFolder(string? name = null)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "forgedesk-tests", name ?? Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
