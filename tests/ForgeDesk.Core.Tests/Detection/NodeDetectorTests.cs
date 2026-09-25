using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Detection.Ecosystems;

namespace ForgeDesk.Core.Tests.Detection;

public class NodeDetectorTests
{
    private const string ViteReactApp = """
        {
          "name": "web",
          "scripts": {
            "dev": "vite",
            "build": "tsc -b && vite build",
            "prebuild": "rimraf dist",
            "test": "vitest run",
            "test:e2e": "playwright test",
            "lint": "eslint .",
            "typecheck": "tsc --noEmit",
            "format": "prettier --write .",
            "preview": "vite preview",
            "clean": "rimraf dist",
            "deploy": "wrangler deploy",
            "postinstall": "husky"
          },
          "dependencies": { "react": "^19.0.0", "react-dom": "^19.0.0" },
          "devDependencies": {
            "vite": "^6.0.0", "typescript": "^5.6.0", "vitest": "^2.1.0", "@playwright/test": "^1.49.0",
            "eslint": "^9.0.0", "prettier": "^3.4.0", "tailwindcss": "^4.0.0"
          }
        }
        """;

    [Fact]
    public async Task Scripts_become_commands_with_categories_from_their_names()
    {
        using var fixture = new DetectionFixture().With("package.json", ViteReactApp).With("package-lock.json", "{}");

        var profile = await fixture.DetectAsync();

        profile.Command("npm:dev").Should().Match<DetectedCommand>(c => c.Category == CommandCategory.Dev && c.CommandLine == "npm run dev" && c.Source == "package.json");
        profile.Command("npm:build").Category.Should().Be(CommandCategory.Build);
        profile.Command("npm:test").CommandLine.Should().Be("npm test");
        profile.Command("npm:test").Category.Should().Be(CommandCategory.Test);
        profile.Command("npm:test:e2e").Category.Should().Be(CommandCategory.Test);
        profile.Command("npm:test:e2e").CommandLine.Should().Be("npm run test:e2e");
        profile.Command("npm:lint").Category.Should().Be(CommandCategory.Lint);
        profile.Command("npm:typecheck").Category.Should().Be(CommandCategory.Lint);
        profile.Command("npm:format").Category.Should().Be(CommandCategory.Format);
        profile.Command("npm:preview").Category.Should().Be(CommandCategory.Run);
        profile.Command("npm:clean").Category.Should().Be(CommandCategory.Clean);
        profile.Command("npm:deploy").Category.Should().Be(CommandCategory.Deploy);
        profile.Command("npm:install").Should().Match<DetectedCommand>(c => c.Category == CommandCategory.Install && c.CommandLine == "npm install" && c.Name == "Install dependencies");
        profile.Command("npm:build").Description.Should().Be("tsc -b && vite build");
    }

    [Fact]
    public async Task Lifecycle_hooks_that_run_automatically_are_not_listed()
    {
        using var fixture = new DetectionFixture().With("package.json", ViteReactApp);

        var profile = await fixture.DetectAsync();

        profile.HasCommand("npm:prebuild").Should().BeFalse();
        profile.HasCommand("npm:postinstall").Should().BeFalse();
    }

    [Fact]
    public async Task Frameworks_and_tools_are_detected_from_dependencies()
    {
        using var fixture = new DetectionFixture().With("package.json", ViteReactApp);

        var profile = await fixture.DetectAsync();

        profile.Technology("React").Kind.Should().Be(TechnologyKind.Framework);
        profile.Technology("Vite").Kind.Should().Be(TechnologyKind.BuildTool);
        profile.Technology("TypeScript").Kind.Should().Be(TechnologyKind.Language);
        profile.Technology("Tailwind CSS");
        profile.Technology("ESLint").Kind.Should().Be(TechnologyKind.Tooling);
        profile.Technology("Prettier");
        profile.Technology("Node.js").Kind.Should().Be(TechnologyKind.Runtime);
        profile.Tests.Frameworks.Should().Contain(["Vitest", "Playwright"]);
        profile.BuildSystems.Should().Contain("npm");
    }

    [Theory]
    [InlineData("next", "Next.js")]
    [InlineData("vue", "Vue")]
    [InlineData("nuxt", "Nuxt")]
    [InlineData("svelte", "Svelte")]
    [InlineData("@sveltejs/kit", "SvelteKit")]
    [InlineData("@angular/core", "Angular")]
    [InlineData("solid-js", "Solid")]
    [InlineData("astro", "Astro")]
    [InlineData("@remix-run/react", "Remix")]
    [InlineData("express", "Express")]
    [InlineData("fastify", "Fastify")]
    [InlineData("@nestjs/core", "NestJS")]
    [InlineData("electron", "Electron")]
    [InlineData("@tauri-apps/api", "Tauri")]
    [InlineData("webpack", "Webpack")]
    [InlineData("esbuild", "esbuild")]
    [InlineData("jest", "Jest")]
    [InlineData("mocha", "Mocha")]
    [InlineData("cypress", "Cypress")]
    [InlineData("@storybook/react", "Storybook")]
    public async Task Known_packages_map_to_technologies(string package, string technology)
    {
        using var fixture = new DetectionFixture().With("package.json", $$"""{ "devDependencies": { "{{package}}": "1.0.0" } }""");

        var profile = await fixture.DetectAsync();

        profile.Technology(technology).Evidence.Should().Contain(package);
    }

    [Theory]
    [InlineData("pnpm-lock.yaml", "pnpm", "pnpm run dev", "pnpm test")]
    [InlineData("yarn.lock", "yarn", "yarn run dev", "yarn test")]
    [InlineData("bun.lockb", "bun", "bun run dev", "bun run test")]
    [InlineData("bun.lock", "bun", "bun run dev", "bun run test")]
    [InlineData("package-lock.json", "npm", "npm run dev", "npm test")]
    public async Task Package_manager_comes_from_the_lockfile(string lockfile, string manager, string dev, string test)
    {
        using var fixture = new DetectionFixture()
            .With("package.json", """{ "scripts": { "dev": "vite", "test": "vitest" } }""")
            .With(lockfile, "lock");

        var profile = await fixture.DetectAsync();

        profile.Command($"{manager}:dev").CommandLine.Should().Be(dev);
        profile.Command($"{manager}:test").CommandLine.Should().Be(test);
        profile.Command($"{manager}:install").CommandLine.Should().Be($"{manager} install");
        profile.BuildSystems.Should().Contain(manager);
    }

    [Fact]
    public async Task PackageManager_field_is_used_without_lockfile()
    {
        using var fixture = new DetectionFixture().With("package.json", """{ "packageManager": "pnpm@9.12.0", "scripts": { "build": "tsc" } }""");

        var profile = await fixture.DetectAsync();

        profile.Command("pnpm:build").CommandLine.Should().Be("pnpm run build");
    }

    [Fact]
    public async Task Workspaces_make_a_monorepo_with_nested_package_commands()
    {
        using var fixture = new DetectionFixture()
            .With("package.json", """{ "private": true, "workspaces": ["apps/*", "packages/*"], "scripts": { "build": "turbo build" }, "devDependencies": { "turbo": "2.0.0" } }""")
            .With("pnpm-lock.yaml", "lockfileVersion: 9")
            .With("apps/web/package.json", """{ "name": "web", "scripts": { "dev": "next dev" }, "dependencies": { "next": "15.0.0" } }""")
            .With("packages/ui/package.json", """{ "name": "ui", "scripts": { "build": "tsup", "test": "vitest" } }""")
            .With("packages/ui/src/index.ts", "export {};");

        var profile = await fixture.DetectAsync();

        profile.IsMonorepo.Should().BeTrue();
        profile.Command("pnpm:build").WorkingDirectory.Should().BeEmpty();
        var webDev = profile.Command("pnpm:apps/web:dev");
        webDev.WorkingDirectory.Should().Be("apps/web");
        webDev.CommandLine.Should().Be("pnpm run dev");
        webDev.Name.Should().Be("dev (apps/web)");
        webDev.Source.Should().Be("apps/web/package.json");
        profile.Command("pnpm:packages/ui:test").CommandLine.Should().Be("pnpm test");
        profile.Technology("Next.js");
        profile.Technology("Turborepo");
        profile.HasCommand("pnpm:apps/web:install").Should().BeFalse("workspace installs happen at the root");
    }

    [Fact]
    public async Task Pnpm_workspace_file_alone_marks_a_monorepo()
    {
        using var fixture = new DetectionFixture()
            .With("package.json", """{ "scripts": {} }""")
            .With("pnpm-workspace.yaml", "packages:\n  - 'packages/*'\n");

        var profile = await fixture.DetectAsync();

        profile.IsMonorepo.Should().BeTrue();
    }

    [Fact]
    public async Task Nested_package_limits_are_respected()
    {
        using var fixture = new DetectionFixture().With("package.json", """{ "workspaces": ["packages/*"] }""");
        for (var i = 0; i < 25; i++)
        {
            fixture.With($"packages/p{i:D2}/package.json", """{ "scripts": { "build": "tsc" } }""");
        }

        fixture.With("a/b/c/d/package.json", """{ "scripts": { "deep": "echo" } }""");

        var profile = await fixture.DetectAsync();

        profile.Commands.Count(c => c.Id.StartsWith("npm:packages/", StringComparison.Ordinal)).Should().Be(NodeDetector.MaxNestedPackages);
        profile.HasCommand("npm:a/b/c/d:deep").Should().BeFalse();
    }

    [Fact]
    public async Task Sub_project_is_found_when_the_root_has_no_package_json()
    {
        using var fixture = new DetectionFixture()
            .With("Api/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />")
            .With("ClientApp/package.json", """{ "scripts": { "start": "ng serve" }, "dependencies": { "@angular/core": "19.0.0" } }""")
            .With("ClientApp/yarn.lock", "");

        var profile = await fixture.DetectAsync();

        var start = profile.Command("yarn:ClientApp:start");
        start.WorkingDirectory.Should().Be("ClientApp");
        start.Category.Should().Be(CommandCategory.Dev);
        profile.Command("yarn:ClientApp:install").WorkingDirectory.Should().Be("ClientApp");
        profile.IsMonorepo.Should().BeFalse();
    }

    [Fact]
    public async Task Broken_package_json_adds_a_note_instead_of_failing()
    {
        using var fixture = new DetectionFixture().With("package.json", "{ \"scripts\": { \"build\": ");

        var profile = await fixture.DetectAsync();

        profile.Notes.Should().ContainSingle(n => n.Contains("package.json", StringComparison.Ordinal));
        profile.Technology("Node.js");
        profile.Commands.Should().NotContain(c => c.Id == "npm:build");
    }

    [Fact]
    public async Task Script_names_with_spaces_are_quoted()
    {
        using var fixture = new DetectionFixture().With("package.json", """{ "scripts": { "build all": "tsc" } }""");

        var profile = await fixture.DetectAsync();

        profile.Command("npm:build all").CommandLine.Should().Be("npm run \"build all\"");
    }
}
