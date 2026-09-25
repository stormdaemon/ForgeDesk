using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Detection.Ecosystems;

namespace ForgeDesk.Core.Tests.Detection;

public class PhpAndRubyDetectorTests
{
    [Fact]
    public async Task Laravel_project_with_composer_scripts()
    {
        using var fixture = new DetectionFixture()
            .With("composer.json", """
                {
                  "require": { "php": "^8.3", "laravel/framework": "^11.0" },
                  "require-dev": { "phpunit/phpunit": "^11.0" },
                  "scripts": {
                    "post-autoload-dump": ["@php artisan package:discover"],
                    "lint": "pint --test",
                    "dev": ["npx concurrently \"php artisan serve\" \"npm run dev\""]
                  }
                }
                """)
            .With("artisan", "#!/usr/bin/env php")
            .With("phpunit.xml", "<phpunit />");

        var profile = await fixture.DetectAsync();

        profile.Command("composer:install").Should().Match<DetectedCommand>(c => c.CommandLine == "composer install" && c.Category == CommandCategory.Install);
        profile.Command("composer:lint").Should().Match<DetectedCommand>(c => c.CommandLine == "composer run-script lint" && c.Category == CommandCategory.Lint);
        profile.Command("composer:dev").Category.Should().Be(CommandCategory.Dev);
        profile.HasCommand("composer:post-autoload-dump").Should().BeFalse();
        profile.Command("artisan:serve").Should().Match<DetectedCommand>(c => c.CommandLine == "php artisan serve" && c.Category == CommandCategory.Dev);
        profile.Command("artisan:test").Category.Should().Be(CommandCategory.Test);
        profile.HasCommand("phpunit:test").Should().BeFalse("Laravel runs PHPUnit through artisan test");
        profile.Technology("Laravel");
        profile.Technology("PHPUnit");
        profile.Technology("PHP").Kind.Should().Be(TechnologyKind.Language);
    }

    [Fact]
    public async Task Plain_phpunit_runs_the_vendor_binary_with_platform_separators()
    {
        using var fixture = new DetectionFixture().With("composer.json", """{ "require-dev": { "phpunit/phpunit": "^11" } }""");

        var profile = await fixture.DetectAsync();

        var expected = OperatingSystem.IsWindows() ? @"vendor\bin\phpunit" : "./vendor/bin/phpunit";
        profile.Command("phpunit:test").CommandLine.Should().Be(expected);
        profile.Tests.Frameworks.Should().Contain("PHPUnit");
    }

    [Fact]
    public async Task Broken_composer_json_is_tolerated()
    {
        using var fixture = new DetectionFixture().With("composer.json", "{ nope");

        var profile = await fixture.DetectAsync();

        profile.Notes.Should().Contain(n => n.Contains("composer.json", StringComparison.Ordinal));
        profile.HasCommand("composer:install").Should().BeTrue();
    }

    [Fact]
    public async Task Rails_app_with_rspec_and_rake_tasks()
    {
        using var fixture = new DetectionFixture()
            .With("Gemfile", "source 'https://rubygems.org'\ngem 'rails', '~> 8.0'\ngroup :test do\n  gem \"rspec-rails\"\nend\n")
            .With("bin/rails", "#!/usr/bin/env ruby")
            .With("Rakefile", "require_relative 'config/application'\nRails.application.load_tasks\n\ndesc 'Seed demo accounts'\ntask :seed_demo do\nend\n")
            .With("spec/models/user_spec.rb", "");

        var profile = await fixture.DetectAsync();

        var rails = OperatingSystem.IsWindows() ? @"ruby bin\rails" : "bin/rails";
        profile.Command("bundler:install").CommandLine.Should().Be("bundle install");
        profile.Command("rails:server").Should().Match<DetectedCommand>(c => c.CommandLine == $"{rails} server" && c.Category == CommandCategory.Dev);
        profile.Command("rails:test").CommandLine.Should().Be($"{rails} test");
        profile.Command("ruby:rspec").Should().Match<DetectedCommand>(c => c.CommandLine == "bundle exec rspec" && c.Category == CommandCategory.Test);
        profile.Command("rake:seed_demo").Should().Match<DetectedCommand>(c => c.CommandLine == "bundle exec rake seed_demo" && c.Description == "Seed demo accounts");
        profile.Technology("Rails");
        profile.Technology("RSpec");
        profile.Tests.Frameworks.Should().Contain("RSpec");
        profile.Tests.Locations.Should().Contain("spec");
    }

    [Fact]
    public void Rake_tasks_are_parsed_with_namespaces_and_task_classes()
    {
        const string rakefile = """
            require "rake/testtask"

            Rake::TestTask.new do |t|
              t.pattern = "test/**/*_test.rb"
            end

            RSpec::Core::RakeTask.new(:spec)

            task default: :test

            namespace :db do
              desc "Reset the database"
              task :reset do
                puts "reset"
              end

              namespace :seed do
                task "demo" => :environment
              end
            end

            task 'release:notes'
            multitask :build_all
            """;

        var tasks = RubyDetector.RakeTasks(rakefile);

        tasks.Select(t => t.Name).Should().Equal("test", "spec", "default", "db:reset", "db:seed:demo", "release:notes", "build_all");
        tasks.Single(t => t.Name == "db:reset").Description.Should().Be("Reset the database");
    }

    [Fact]
    public async Task Rake_default_task_runs_plain_rake_without_bundler()
    {
        using var fixture = new DetectionFixture().With("Rakefile", "task default: [:clean]\ntask :clean do\nend\n");

        var profile = await fixture.DetectAsync();

        profile.Command("rake:default").CommandLine.Should().Be("rake");
        profile.Command("rake:clean").Should().Match<DetectedCommand>(c => c.CommandLine == "rake clean" && c.Category == CommandCategory.Clean);
    }
}
