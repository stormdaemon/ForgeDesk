using System.Text.RegularExpressions;

namespace ForgeDesk.Core.Detection.Ecosystems;

/// <summary>Ruby: Gemfile gems, Rakefile tasks (parsed, because "rake -T" is slow), Rails, RSpec.</summary>
internal sealed partial class RubyDetector : IEcosystemDetector
{
    public const int MaxRakeTasks = 30;

    public string Name => "Ruby";

    public async Task ContributeAsync(DetectionContext context, CancellationToken cancellationToken)
    {
        var hasGemfile = context.Exists("Gemfile");
        var rakefile = context.FirstExisting("Rakefile", "rakefile", "Rakefile.rb");
        if (!hasGemfile && rakefile is null)
        {
            return;
        }

        context.AddTechnology("Ruby", TechnologyKind.Language, hasGemfile ? "Gemfile" : rakefile!);
        var gems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (hasGemfile)
        {
            context.AddBuildSystem("Bundler");
            context.AddTechnology("Bundler", TechnologyKind.PackageManager, "Gemfile");
            Add(context, "bundler:install", "Install dependencies", "bundle install", CommandCategory.Install, "Gemfile");
            if (await context.ReadTextAsync("Gemfile", cancellationToken).ConfigureAwait(false) is { } gemfile)
            {
                gems.UnionWith(GemDeclaration().Matches(gemfile).Select(m => m.Groups["name"].Value));
            }
        }

        var bundleExec = hasGemfile ? "bundle exec " : string.Empty;
        var isRails = gems.Contains("rails") || context.Exists("bin/rails");
        if (isRails)
        {
            context.AddTechnology("Rails", TechnologyKind.Framework, gems.Contains("rails") ? "Gemfile" : "bin/rails");
            var rails = context.Exists("bin/rails")
                ? (OperatingSystem.IsWindows() ? @"ruby bin\rails" : "bin/rails")
                : $"{bundleExec}rails";
            Add(context, "rails:server", "Rails server", $"{rails} server", CommandCategory.Dev, "Gemfile");
            Add(context, "rails:test", "Rails tests", $"{rails} test", CommandCategory.Test, "Gemfile");
            Add(context, "rails:migrate", "Apply migrations", $"{rails} db:migrate", CommandCategory.Run, "Gemfile");
        }

        if (gems.Contains("sinatra"))
        {
            context.AddTechnology("Sinatra", TechnologyKind.Framework, "Gemfile");
        }

        if (gems.Contains("rspec") || gems.Contains("rspec-rails") || gems.Contains("rspec-core") || context.Exists(".rspec"))
        {
            context.AddTechnology("RSpec", TechnologyKind.TestFramework, gems.Contains("rspec-rails") ? "Gemfile" : ".rspec");
            context.AddTestFramework("RSpec");
            Add(context, "ruby:rspec", "RSpec", $"{bundleExec}rspec", CommandCategory.Test, "Gemfile");
        }

        if (gems.Contains("minitest") || (isRails && context.HasDirectory("test")))
        {
            context.AddTestFramework("Minitest");
        }

        if (rakefile is not null && await context.ReadTextAsync(rakefile, cancellationToken).ConfigureAwait(false) is { } rakeText)
        {
            context.AddTechnology("Rake", TechnologyKind.BuildTool, rakefile);
            foreach (var task in RakeTasks(rakeText).Take(MaxRakeTasks))
            {
                var commandLine = task.Name == "default" ? $"{bundleExec}rake" : $"{bundleExec}rake {task.Name}";
                context.AddCommand(new DetectedCommand
                {
                    Id = $"rake:{task.Name}",
                    Name = task.Name == "default" ? "rake (default)" : task.Name,
                    CommandLine = commandLine,
                    Category = CommandCategorizer.FromName(task.Name.Split(':')[^1]),
                    Source = rakefile,
                    Description = task.Description,
                });
            }
        }
    }

    /// <summary>
    /// Task names from "task :name", "task name: [...]" and "task 'name'" lines, prefixed with the
    /// enclosing "namespace :db do" blocks (tracked by indentation), with their "desc" text.
    /// </summary>
    internal static IReadOnlyList<(string Name, string? Description)> RakeTasks(string rakefile)
    {
        var tasks = new List<(string, string?)>();
        var namespaces = new Stack<(int Indent, string Name)>();
        string? pendingDescription = null;

        foreach (var rawLine in rakefile.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var indent = rawLine.Length - rawLine.TrimStart().Length;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line == "end" || line.StartsWith("end ", StringComparison.Ordinal))
            {
                if (namespaces.Count > 0 && namespaces.Peek().Indent == indent)
                {
                    namespaces.Pop();
                }

                continue;
            }

            if (NamespaceLine().Match(line) is { Success: true } ns)
            {
                namespaces.Push((indent, ns.Groups["name"].Value));
                continue;
            }

            if (DescLine().Match(line) is { Success: true } desc)
            {
                pendingDescription = desc.Groups["text"].Value;
                continue;
            }

            var taskName = TaskLine().Match(line) is { Success: true } task ? task.Groups["name"].Value
                : TaskClassLine().Match(line) is { Success: true } taskClass ? (taskClass.Groups["name"].Success ? taskClass.Groups["name"].Value : DefaultTaskName(taskClass.Groups["class"].Value))
                : null;
            if (taskName is { } name)
            {
                var fullName = string.Join(':', namespaces.Reverse().Select(n => n.Name).Append(name));
                if (!tasks.Any(t => t.Item1 == fullName))
                {
                    tasks.Add((fullName, pendingDescription));
                }
            }

            pendingDescription = null;
        }

        return tasks;
    }

    // Rake::TestTask.new defines "test", RSpec::Core::RakeTask.new defines "spec"… unless a name is given.
    private static string DefaultTaskName(string taskClass) => taskClass switch
    {
        "RSpec::Core::RakeTask" => "spec",
        "RuboCop::RakeTask" => "rubocop",
        _ => "test",
    };

    private static void Add(DetectionContext context, string id, string name, string commandLine, CommandCategory category, string source) =>
        context.AddCommand(new DetectedCommand { Id = id, Name = name, CommandLine = commandLine, Category = category, Source = source });

    [GeneratedRegex(@"^\s*gem\s+['""](?<name>[A-Za-z0-9_.-]+)['""]", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex GemDeclaration();

    [GeneratedRegex(@"^namespace\s+(?::(?<name>[A-Za-z0-9_]+)|['""](?<name>[A-Za-z0-9_:-]+)['""])", RegexOptions.CultureInvariant)]
    private static partial Regex NamespaceLine();

    [GeneratedRegex(@"^desc\s+['""](?<text>.*)['""]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DescLine();

    [GeneratedRegex(@"^(?:multitask|task)[\s(]+(?::(?<name>[A-Za-z0-9_]+)|['""](?<name>[A-Za-z0-9_:-]+)['""]|(?<name>[A-Za-z0-9_]+):)", RegexOptions.CultureInvariant)]
    private static partial Regex TaskLine();

    [GeneratedRegex(@"^(?<class>Rake::TestTask|RSpec::Core::RakeTask|RuboCop::RakeTask)\.new(?:\(\s*:(?<name>[A-Za-z0-9_]+))?", RegexOptions.CultureInvariant)]
    private static partial Regex TaskClassLine();
}
