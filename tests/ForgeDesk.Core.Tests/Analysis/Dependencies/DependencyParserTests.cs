using ForgeDesk.Core.Analysis;
using ForgeDesk.Core.Analysis.Dependencies;

namespace ForgeDesk.Core.Tests.Analysis.Dependencies;

public class DependencyParserTests
{
    // --- npm ----------------------------------------------------------------------------------

    [Fact]
    public void Npm_reads_dependencies_and_dev_dependencies()
    {
        var deps = NpmDependencyParser.Parse(
            """
            {
              // comments and trailing commas are tolerated
              "name": "web",
              "dependencies": { "react": "^18.2.0", "@scope/ui": "workspace:*" },
              "devDependencies": { "vitest": "1.6.0", },
              "peerDependencies": { "ignored": "1" }
            }
            """, "web/package.json");

        Summarize(deps).Should().Equal(
            ("react", "^18.2.0", false),
            ("@scope/ui", "workspace:*", false),
            ("vitest", "1.6.0", true));
        deps.Should().OnlyContain(d => d.Ecosystem == "npm" && d.Manifest == "web/package.json");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("{ \"dependencies\": [\"a\"] }")]
    [InlineData("")]
    public void Npm_ignores_malformed_manifests(string content) =>
        NpmDependencyParser.Parse(content, "package.json").Should().BeEmpty();

    // --- Composer -----------------------------------------------------------------------------

    [Fact]
    public void Composer_skips_the_platform_and_extensions()
    {
        var deps = ComposerDependencyParser.Parse(
            """
            {
              "require": { "php": ">=8.1", "ext-json": "*", "laravel/framework": "^10.0", "composer-runtime-api": "^2" },
              "require-dev": { "phpunit/phpunit": "^10.1" }
            }
            """, "composer.json");

        Summarize(deps).Should().Equal(("laravel/framework", "^10.0", false), ("phpunit/phpunit", "^10.1", true));
    }

    // --- Cargo --------------------------------------------------------------------------------

    [Fact]
    public void Cargo_reads_every_dependency_table_form()
    {
        var deps = CargoDependencyParser.Parse(
            """
            [package]
            name = "tool"
            version = "0.1.0"

            [dependencies]
            serde = { version = "1.0", features = ["derive"] }
            anyhow = "1"
            local = { path = "../local" }
            json = { package = "serde_json", version = "1.0.100" }
            tokio.workspace = true

            [dependencies.clap]
            version = "4.5"
            features = ["derive"]

            [dev-dependencies]
            insta = "1.39"

            [build-dependencies]
            cc = "1.0"

            [target.'cfg(windows)'.dependencies]
            windows-sys = { version = "0.52" }
            """, "crates/tool/Cargo.toml", new Dictionary<string, string> { ["tokio"] = "1.38" });

        Summarize(deps).Should().Equal(
            ("serde", "1.0", false),
            ("anyhow", "1", false),
            ("local", null, false),
            ("serde_json", "1.0.100", false),
            ("tokio", "1.38", false),
            ("clap", "4.5", false),
            ("insta", "1.39", true),
            ("cc", "1.0", true),
            ("windows-sys", "0.52", false));
    }

    [Fact]
    public void Cargo_workspace_versions_are_read_from_the_workspace_table_only()
    {
        const string root =
            """
            [workspace]
            members = ["crates/*"]

            [workspace.dependencies]
            tokio = { version = "1.38", features = ["full"] }
            serde = "1.0.200"
            """;

        CargoDependencyParser.ReadWorkspaceVersions(root).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["tokio"] = "1.38",
            ["serde"] = "1.0.200",
        });
        CargoDependencyParser.Parse(root, "Cargo.toml").Should().BeEmpty("a virtual manifest declares no dependencies of its own");
        CargoDependencyParser.Parse("[dependencies]\ntokio = { workspace = true }", "a/Cargo.toml").Single().Version
            .Should().Be("workspace", "the version is unknown without the workspace root");
    }

    // --- Go -----------------------------------------------------------------------------------

    [Fact]
    public void GoMod_reads_require_blocks_and_single_lines_without_indirect_ones()
    {
        var deps = GoModDependencyParser.Parse(
            """
            module example.com/tool

            go 1.22

            require github.com/spf13/cobra v1.8.0

            require (
                github.com/stretchr/testify v1.9.0
                golang.org/x/sys v0.20.0 // indirect
                "github.com/quoted/mod" v0.1.0 // pinned for #12
            )

            replace github.com/spf13/cobra => ../cobra
            """, "go.mod");

        Summarize(deps).Should().Equal(
            ("github.com/spf13/cobra", "v1.8.0", false),
            ("github.com/stretchr/testify", "v1.9.0", false),
            ("github.com/quoted/mod", "v0.1.0", false));
        deps.Should().OnlyContain(d => d.Ecosystem == "Go");
    }

    [Fact]
    public void GoMod_handles_crlf_and_one_line_blocks()
    {
        var deps = GoModDependencyParser.Parse("module m\r\nrequire (\r\n\ta.com/b v1.0.0\r\n)\r\nrequire ()\r\n", "go.mod");

        Summarize(deps).Should().Equal(("a.com/b", "v1.0.0", false));
    }

    // --- pip ----------------------------------------------------------------------------------

    [Fact]
    public void Requirements_reads_names_and_versions_and_skips_options()
    {
        var deps = PipRequirementsParser.Parse(
            """
            # pinned
            requests==2.31.0
            Django>=4.2,<5 ; python_version >= "3.10"
            uvicorn[standard]~=0.29  # server
            numpy
            -r base.txt
            --index-url https://pypi.example.com/simple
            -e git+https://github.com/o/r.git#egg=r
            ./local-package
            https://example.com/pkg.whl
            mypkg @ https://example.com/mypkg-1.0.tar.gz
            hashed==1.0 \
                --hash=sha256:abc
            """, "requirements.txt");

        Summarize(deps).Should().Equal(
            ("requests", "2.31.0", false),
            ("Django", ">=4.2,<5", false),
            ("uvicorn", "~=0.29", false),
            ("numpy", null, false),
            ("mypkg", null, false),
            ("hashed", "1.0", false));
    }

    [Theory]
    [InlineData("requirements-dev.txt", true)]
    [InlineData("requirements_test.txt", true)]
    [InlineData("requirements-docs.txt", true)]
    [InlineData("requirements.txt", false)]
    [InlineData("requirements-prod.txt", false)]
    public void Requirements_files_named_for_development_hold_dev_dependencies(string fileName, bool expected) =>
        PipRequirementsParser.Parse("pytest", "backend/" + fileName).Single().IsDevelopment.Should().Be(expected);

    // --- pyproject ----------------------------------------------------------------------------

    [Fact]
    public void PyProject_reads_pep621_optional_groups_and_dependency_groups()
    {
        var deps = PyProjectDependencyParser.Parse(
            """
            [project]
            name = "svc"
            dependencies = [
                "fastapi>=0.110",
                "pydantic==2.7.1",
            ]

            [project.optional-dependencies]
            test = ["pytest>=8"]
            postgres = ["psycopg[binary]"]

            [dependency-groups]
            lint = ["ruff==0.4.4", { include-group = "test" }]
            """, "pyproject.toml");

        Summarize(deps).Should().Equal(
            ("fastapi", ">=0.110", false),
            ("pydantic", "2.7.1", false),
            ("pytest", ">=8", true),
            ("psycopg", null, false),
            ("ruff", "0.4.4", true));
        deps.Should().OnlyContain(d => d.Ecosystem == "PyPI");
    }

    [Fact]
    public void PyProject_reads_poetry_tables()
    {
        var deps = PyProjectDependencyParser.Parse(
            """
            [tool.poetry.dependencies]
            python = "^3.11"
            httpx = "^0.27"
            rich = { version = "^13.7", optional = true }
            local = { path = "../local" }

            [tool.poetry.dev-dependencies]
            black = "*"

            [tool.poetry.group.test.dependencies]
            pytest = "^8.2"
            """, "pyproject.toml");

        Summarize(deps).Should().Equal(
            ("httpx", "^0.27", false),
            ("rich", "^13.7", false),
            ("local", null, false),
            ("black", null, true),
            ("pytest", "^8.2", true));
    }

    // --- MSBuild ------------------------------------------------------------------------------

    [Fact]
    public void MsBuild_reads_package_references_in_every_form()
    {
        var deps = MsBuildDependencyParser.Parse(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.0.0" />
                <PackageReference Include="Dapper">
                  <Version>2.1.35</Version>
                </PackageReference>
                <PackageReference Include="Octokit" />
                <PackageReference Include="Pinned" VersionOverride="9.9.9" />
                <PackageReference Include="Microsoft.SourceLink.GitHub" Version="8.0.0" PrivateAssets="All" />
                <PackageReference Update="Serilog" Version="5.0.0" />
              </ItemGroup>
            </Project>
            """, "src/App/App.csproj", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["octokit"] = "14.0.0", ["Pinned"] = "1.0.0" });

        Summarize(deps).Should().Equal(
            ("Serilog", "4.0.0", false),
            ("Dapper", "2.1.35", false),
            ("Octokit", "14.0.0", false),
            ("Pinned", "9.9.9", false),
            ("Microsoft.SourceLink.GitHub", "8.0.0", true));
        deps.Should().OnlyContain(d => d.Ecosystem == "NuGet");
    }

    [Fact]
    public void MsBuild_marks_test_project_packages_as_development()
    {
        var deps = MsBuildDependencyParser.Parse(
            """
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup>
                <PackageReference Include="xunit.v3" Version="4.0.1" />
                <PackageReference Include="NSubstitute" Version="6.2.0" />
              </ItemGroup>
            </Project>
            """, "tests/App.Tests/App.Tests.csproj");

        deps.Should().HaveCount(2).And.OnlyContain(d => d.IsDevelopment);
    }

    [Fact]
    public void MsBuild_reads_central_package_versions()
    {
        var versions = MsBuildDependencyParser.ReadCentralVersions(
            """
            <Project>
              <ItemGroup Label="Core">
                <PackageVersion Include="Dapper" Version="2.1.89" />
                <PackageVersion Include="Octokit"><Version>14.0.0</Version></PackageVersion>
              </ItemGroup>
            </Project>
            """);

        versions.Should().BeEquivalentTo(new Dictionary<string, string> { ["Dapper"] = "2.1.89", ["Octokit"] = "14.0.0" });
    }

    [Fact]
    public void MsBuild_refuses_dtds_and_tolerates_malformed_xml()
    {
        const string withDtd =
            """
            <?xml version="1.0"?>
            <!DOCTYPE lolz [<!ENTITY lol "lol">]>
            <Project><ItemGroup><PackageReference Include="&lol;" Version="1" /></ItemGroup></Project>
            """;

        MsBuildDependencyParser.Parse(withDtd, "a.csproj").Should().BeEmpty();
        MsBuildDependencyParser.Parse("<Project><ItemGroup>", "a.csproj").Should().BeEmpty();
    }

    // --- Maven --------------------------------------------------------------------------------

    [Fact]
    public void Maven_reads_project_dependencies_with_properties_and_scopes()
    {
        var deps = MavenDependencyParser.Parse(
            """
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <version>2.3.0</version>
              <properties>
                <spring.version>6.1.8</spring.version>
              </properties>
              <dependencyManagement>
                <dependencies>
                  <dependency><groupId>managed</groupId><artifactId>only</artifactId><version>1</version></dependency>
                </dependencies>
              </dependencyManagement>
              <dependencies>
                <dependency>
                  <groupId>org.springframework</groupId>
                  <artifactId>spring-core</artifactId>
                  <version>${spring.version}</version>
                </dependency>
                <dependency>
                  <groupId>com.example</groupId>
                  <artifactId>sibling</artifactId>
                  <version>${project.version}</version>
                </dependency>
                <dependency>
                  <groupId>org.junit.jupiter</groupId>
                  <artifactId>junit-jupiter</artifactId>
                  <scope>test</scope>
                </dependency>
                <dependency>
                  <groupId>x</groupId>
                  <artifactId>unknown-prop</artifactId>
                  <version>${missing}</version>
                </dependency>
              </dependencies>
              <build><plugins><plugin><dependencies>
                <dependency><groupId>plugin</groupId><artifactId>dep</artifactId></dependency>
              </dependencies></plugin></plugins></build>
            </project>
            """, "pom.xml");

        Summarize(deps).Should().Equal(
            ("org.springframework:spring-core", "6.1.8", false),
            ("com.example:sibling", "2.3.0", false),
            ("org.junit.jupiter:junit-jupiter", null, true),
            ("x:unknown-prop", "${missing}", false));
    }

    // --- Gradle -------------------------------------------------------------------------------

    [Fact]
    public void Gradle_reads_string_and_map_notations_in_both_dsls()
    {
        var deps = GradleDependencyParser.Parse(
            """
            dependencies {
                implementation 'com.google.guava:guava:33.2.0-jre'
                implementation("org.jetbrains.kotlin:kotlin-stdlib")
                api "com.squareup.okhttp3:okhttp:4.12.0@jar"
                testImplementation("org.junit.jupiter:junit-jupiter:5.10.2")
                androidTestImplementation 'androidx.test:runner:1.5.2'
                implementation group: 'org.slf4j', name: 'slf4j-api', version: '2.0.13'
                runtimeOnly(group = "org.postgresql", name = "postgresql", version = "42.7.3")
                implementation platform('org.springframework.boot:spring-boot-dependencies:3.3.0')
                implementation project(':core')
                implementation files('libs/local.jar')
                compileSdk 34
            }
            """, "app/build.gradle");

        Summarize(deps).Should().Equal(
            ("com.google.guava:guava", "33.2.0-jre", false),
            ("org.jetbrains.kotlin:kotlin-stdlib", null, false),
            ("com.squareup.okhttp3:okhttp", "4.12.0", false),
            ("org.junit.jupiter:junit-jupiter", "5.10.2", true),
            ("androidx.test:runner", "1.5.2", true),
            ("org.slf4j:slf4j-api", "2.0.13", false),
            ("org.postgresql:postgresql", "42.7.3", false));
    }

    [Fact]
    public void Gradle_resolves_version_catalog_accessors_and_bundles()
    {
        var catalog = GradleVersionCatalog.Parse(
            """
            [versions]
            kotlin = "2.0.0"
            compose = { strictly = "1.6.7" }

            [libraries]
            androidx-core-ktx = { module = "androidx.core:core-ktx", version = "1.13.1" }
            kotlin_stdlib = { group = "org.jetbrains.kotlin", name = "kotlin-stdlib", version.ref = "kotlin" }
            compose-ui = { module = "androidx.compose.ui:ui", version = { ref = "compose" } }
            compose-material = "androidx.compose.material:material:1.6.7"

            [bundles]
            compose = ["compose-ui", "compose-material"]

            [plugins]
            android = { id = "com.android.application", version = "8.4.0" }
            """);

        var deps = GradleDependencyParser.Parse(
            """
            dependencies {
                implementation(libs.androidx.core.ktx)
                implementation(libs.kotlin.stdlib)
                implementation(libs.bundles.compose)
                testImplementation(libs.unknown.lib)
            }
            """, "app/build.gradle.kts", catalog);

        Summarize(deps).Should().Equal(
            ("androidx.core:core-ktx", "1.13.1", false),
            ("org.jetbrains.kotlin:kotlin-stdlib", "2.0.0", false),
            ("androidx.compose.ui:ui", "1.6.7", false),
            ("androidx.compose.material:material", "1.6.7", false));
    }

    // --- Gemfile ------------------------------------------------------------------------------

    [Fact]
    public void Gemfile_reads_versions_and_development_groups()
    {
        var deps = GemfileDependencyParser.Parse(
            """
            source "https://rubygems.org"
            ruby "3.3.0"

            gem "rails", "~> 7.1.3", ">= 7.1.3.2"
            gem 'pg', '~> 1.1' # database
            gem "bootsnap", require: false
            gem "rspec-rails", group: :test
            gem "rubocop", groups: [:development, :test], require: false
            gem "sidekiq", github: "sidekiq/sidekiq"

            group :development, :test do
              gem "debug", platforms: %i[ mri windows ]
              platforms :mri do
                gem "byebug"
              end
            end

            group :production do
              gem "lograge"
            end

            gem "puma", ">= 5.0"
            """, "Gemfile");

        Summarize(deps).Should().Equal(
            ("rails", "~> 7.1.3, >= 7.1.3.2", false),
            ("pg", "~> 1.1", false),
            ("bootsnap", null, false),
            ("rspec-rails", null, true),
            ("rubocop", null, true),
            ("sidekiq", null, false),
            ("debug", null, true),
            ("byebug", null, true),
            ("lograge", null, false),
            ("puma", ">= 5.0", false));
        deps.Should().OnlyContain(d => d.Ecosystem == "RubyGems");
    }

    private static List<(string Name, string? Version, bool IsDevelopment)> Summarize(IEnumerable<DependencyInfo> deps) =>
        deps.Select(d => (d.Name, d.Version, d.IsDevelopment)).ToList();
}
