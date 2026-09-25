using System.Collections.Frozen;

namespace ForgeDesk.Core.Detection;

/// <summary>Guesses the category of a task from its name (npm scripts, Make targets, just recipes…).</summary>
internal static class CommandCategorizer
{
    private static readonly char[] Separators = [':', '-', '_', '.', ' ', '/'];

    private static readonly FrozenSet<string> PackageNames = new[]
    {
        "electron:build", "electron:dist", "electron:package", "electron:make", "electron-build", "electron-pack",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> LintNames = new[] { "type-check", "check-types", "types:check" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> PackageWords = new[] { "package", "pack", "dist", "make" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> DevWords = new[] { "dev", "start", "serve", "watch", "develop", "storybook" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> TestWords = new[] { "e2e", "coverage", "cov", "cy", "cypress", "playwright", "spec", "specs", "jest", "vitest" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> LintWords = new[] { "typecheck", "tsc", "check", "types", "stylelint", "eslint", "vet", "clippy", "analyze", "analyse" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> FormatWords = new[] { "format", "fmt", "prettier", "prettify" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> CleanWords = new[] { "clean", "distclean", "clear", "purge" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> DeployWords = new[] { "deploy", "release", "publish", "ship" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> InstallWords = new[] { "install", "setup", "bootstrap", "deps", "restore" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static CommandCategory FromName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var trimmed = name.Trim();
        if (PackageNames.Contains(trimmed))
        {
            return CommandCategory.Package;
        }

        if (LintNames.Contains(trimmed))
        {
            return CommandCategory.Lint;
        }

        var tokens = trimmed.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return CommandCategory.Run;
        }

        if (tokens.Any(CleanWords.Contains))
        {
            return CommandCategory.Clean;
        }

        // The leading word usually says what a task does ("test:unit", "build-docs"); namespaced
        // tasks put it last instead ("docker:build", "web:dev").
        var category = Classify(tokens[0]);
        return category == CommandCategory.Run && tokens.Length > 1 ? Classify(tokens[^1]) : category;
    }

    private static CommandCategory Classify(string word)
    {
        if (PackageWords.Contains(word))
        {
            return CommandCategory.Package;
        }

        if (DevWords.Contains(word))
        {
            return CommandCategory.Dev;
        }

        if (word.StartsWith("build", StringComparison.OrdinalIgnoreCase) || word.Equals("compile", StringComparison.OrdinalIgnoreCase))
        {
            return CommandCategory.Build;
        }

        if (word.StartsWith("test", StringComparison.OrdinalIgnoreCase) || TestWords.Contains(word))
        {
            return CommandCategory.Test;
        }

        if (word.StartsWith("lint", StringComparison.OrdinalIgnoreCase) || LintWords.Contains(word))
        {
            return CommandCategory.Lint;
        }

        if (FormatWords.Contains(word) || word.StartsWith("format", StringComparison.OrdinalIgnoreCase))
        {
            return CommandCategory.Format;
        }

        if (DeployWords.Contains(word))
        {
            return CommandCategory.Deploy;
        }

        if (InstallWords.Contains(word))
        {
            return CommandCategory.Install;
        }

        return CommandCategory.Run;
    }
}
