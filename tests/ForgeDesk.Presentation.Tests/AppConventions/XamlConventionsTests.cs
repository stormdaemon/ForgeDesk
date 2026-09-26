using System.Text.RegularExpressions;

namespace ForgeDesk.Presentation.Tests.AppConventions;

/// <summary>
/// Static checks on the WPF app's XAML that catch mistakes the compiler accepts but that break the
/// UI at runtime (the app itself can only run on Windows).
/// </summary>
public partial class XamlConventionsTests
{
    private static readonly string AppDirectory = FindAppDirectory();

    private static string FindAppDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "ForgeDesk.App")))
        {
            dir = dir.Parent;
        }

        return dir is null ? string.Empty : Path.Combine(dir.FullName, "src", "ForgeDesk.App");
    }

    public static TheoryData<string> XamlFilesWithClass()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(AppDirectory, "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            if (File.ReadAllText(file).Contains("x:Class=", StringComparison.Ordinal) && !file.EndsWith("App.xaml", StringComparison.Ordinal))
            {
                data.Add(Path.GetRelativePath(AppDirectory, file));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(XamlFilesWithClass))]
    public void Every_xaml_class_calls_InitializeComponent(string relativePath)
    {
        var codeBehind = Path.Combine(AppDirectory, relativePath + ".cs");
        File.Exists(codeBehind).Should().BeTrue($"{relativePath} declares x:Class, so it needs a code-behind that calls InitializeComponent()");
        File.ReadAllText(codeBehind).Should().Contain("InitializeComponent()", $"{relativePath} would otherwise render empty");
    }

    [Fact]
    public void App_directory_is_found() => Directory.Exists(AppDirectory).Should().BeTrue();
}
