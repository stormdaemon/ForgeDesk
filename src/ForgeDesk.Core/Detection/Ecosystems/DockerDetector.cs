using System.Text;

namespace ForgeDesk.Core.Detection.Ecosystems;

/// <summary>Docker: image build from the root Dockerfile, Compose services.</summary>
internal sealed class DockerDetector : IEcosystemDetector
{
    private static readonly string[] ComposeFiles = ["compose.yaml", "compose.yml", "docker-compose.yml", "docker-compose.yaml"];

    public string Name => "Docker";

    public Task ContributeAsync(DetectionContext context, CancellationToken cancellationToken)
    {
        var dockerfile = context.FirstExisting("Dockerfile", "Containerfile");
        if (dockerfile is not null)
        {
            context.AddTechnology("Docker", TechnologyKind.Infrastructure, dockerfile);
            var image = ImageName(Path.GetFileName(context.Root));
            var fileArgument = dockerfile.Equals("Dockerfile", StringComparison.Ordinal) ? string.Empty : $" -f {dockerfile}";
            Add(context, "docker:build", "Build image", $"docker build{fileArgument} -t {image} .", CommandCategory.Package, dockerfile);
        }

        // "docker compose" only picks these names up by default, so no -f is needed.
        var compose = context.FirstExisting(ComposeFiles);
        if (compose is not null)
        {
            context.AddTechnology("Docker Compose", TechnologyKind.Infrastructure, compose);
            Add(context, "compose:up", "Start services", "docker compose up", CommandCategory.Run, compose);
            Add(context, "compose:down", "Stop services", "docker compose down", CommandCategory.Other, compose);
            Add(context, "compose:build", "Build services", "docker compose build", CommandCategory.Build, compose);
        }

        return Task.CompletedTask;
    }

    /// <summary>Docker image names must be lowercase [a-z0-9._-] and start with a letter or digit.</summary>
    internal static string ImageName(string folderName)
    {
        var builder = new StringBuilder();
        foreach (var c in folderName.ToLowerInvariant())
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-')
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var name = builder.ToString().Trim('-', '.', '_');
        return name.Length == 0 ? "app" : name;
    }

    private static void Add(DetectionContext context, string id, string name, string commandLine, CommandCategory category, string source) =>
        context.AddCommand(new DetectedCommand { Id = id, Name = name, CommandLine = commandLine, Category = category, Source = source });
}
