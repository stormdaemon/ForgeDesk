using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Detection.Ecosystems;

namespace ForgeDesk.Core.Tests.Detection;

public class PythonDetectorTests
{
    [Fact]
    public async Task Poetry_project_with_tools_uses_poetry_run()
    {
        using var fixture = new DetectionFixture()
            .With("pyproject.toml", """
                [tool.poetry]
                name = "api"

                [tool.poetry.dependencies]
                python = "^3.12"
                fastapi = "^0.115"

                [tool.poetry.group.dev.dependencies]
                pytest = "^8.0"
                ruff = "^0.7"
                mypy = "^1.13"

                [tool.black]
                line-length = 100

                [build-system]
                requires = ["poetry-core"]
                build-backend = "poetry.core.masonry.api"
                """)
            .With("poetry.lock", "")
            .With("app/main.py", "from fastapi import FastAPI\n\napi = FastAPI(title=\"x\")\n")
            .With("tests/test_api.py", "def test_ok(): pass\n");

        var profile = await fixture.DetectAsync();

        profile.Command("python:install").CommandLine.Should().Be("poetry install");
        profile.Command("python:pytest").Should().Match<DetectedCommand>(c => c.CommandLine == "poetry run pytest" && c.Category == CommandCategory.Test);
        profile.Command("python:ruff").Should().Match<DetectedCommand>(c => c.CommandLine == "poetry run ruff check ." && c.Category == CommandCategory.Lint);
        profile.Command("python:ruff-format").Category.Should().Be(CommandCategory.Format);
        profile.Command("python:black").CommandLine.Should().Be("poetry run black .");
        profile.Command("python:mypy").Category.Should().Be(CommandCategory.Lint);
        profile.Command("python:uvicorn").Should().Match<DetectedCommand>(c => c.CommandLine == "poetry run uvicorn app.main:api --reload" && c.Category == CommandCategory.Dev);
        profile.Technology("FastAPI").Kind.Should().Be(TechnologyKind.Framework);
        profile.Technology("Poetry").Kind.Should().Be(TechnologyKind.PackageManager);
        profile.BuildSystems.Should().Contain("Poetry");
        profile.Tests.Frameworks.Should().Contain("pytest");
        profile.Tests.Locations.Should().Contain("tests");
    }

    [Fact]
    public async Task Requirements_and_django_use_python_module_invocations()
    {
        using var fixture = new DetectionFixture()
            .With("requirements.txt", "# web\nDjango>=5.0,<6\npsycopg[binary]==3.2.1\n-r requirements-dev.txt\n")
            .With("requirements-dev.txt", "pytest-django\nflake8 ; python_version > '3.8'\n")
            .With("manage.py", "import django\n")
            .With("setup.cfg", "[flake8]\nmax-line-length = 120\n");

        var profile = await fixture.DetectAsync();

        profile.Command("python:install").CommandLine.Should().Be("python -m pip install -r requirements.txt");
        profile.Command("python:django-runserver").Should().Match<DetectedCommand>(c => c.CommandLine == "python manage.py runserver" && c.Category == CommandCategory.Dev);
        profile.Command("python:django-test").Category.Should().Be(CommandCategory.Test);
        profile.Command("python:django-migrate").CommandLine.Should().Be("python manage.py migrate");
        profile.Command("python:flake8").CommandLine.Should().Be("python -m flake8");
        profile.Technology("Django");
        profile.BuildSystems.Should().Contain("pip");
    }

    [Fact]
    public async Task Django_project_one_folder_down_runs_from_its_folder()
    {
        using var fixture = new DetectionFixture()
            .With("backend/manage.py", "import django\n")
            .With("frontend/index.html", "<html></html>");

        var profile = await fixture.DetectAsync();

        profile.Command("python:django-runserver").Should().Match<DetectedCommand>(c =>
            c.CommandLine == "python manage.py runserver" && c.WorkingDirectory == "backend" && c.Source == "backend/manage.py");
        profile.Technology("Django").Evidence.Should().Be("backend/manage.py");
    }

    [Theory]
    [InlineData("uv.lock", "[project]\nname = \"x\"\ndependencies = [\"flask>=3\"]\n", "uv sync", "uv run ")]
    [InlineData("pdm.lock", "[project]\nname = \"x\"\ndependencies = [\"flask>=3\"]\n", "pdm install", "pdm run ")]
    [InlineData("Pipfile.lock", "", "pipenv install --dev", "pipenv run ")]
    public async Task Environment_managers_drive_install_and_run_prefix(string marker, string pyproject, string install, string prefix)
    {
        using var fixture = new DetectionFixture().With(marker, "").With("app.py", "from flask import Flask\n");
        if (pyproject.Length > 0)
        {
            fixture.With("pyproject.toml", pyproject);
        }
        else
        {
            fixture.With("Pipfile", "[packages]\nflask = \"*\"\n\n[dev-packages]\npytest = \"*\"\n");
        }

        var profile = await fixture.DetectAsync();

        profile.Command("python:install").CommandLine.Should().Be(install);
        profile.Command("python:flask-run").CommandLine.Should().Be($"{prefix}flask run --debug");
        profile.Technology("Flask");
    }

    [Fact]
    public async Task Hatch_backend_and_pytest_config_section()
    {
        using var fixture = new DetectionFixture().With("pyproject.toml", """
            [project]
            name = "lib"
            dependencies = []

            [project.optional-dependencies]
            test = ["pytest>=8", "black"]

            [tool.pytest.ini_options]
            addopts = "-q"

            [build-system]
            requires = ["hatchling"]
            build-backend = "hatchling.build"
            """);

        var profile = await fixture.DetectAsync();

        profile.BuildSystems.Should().Contain("Hatch");
        profile.Command("python:pytest").CommandLine.Should().Be("hatch run pytest");
        profile.Command("python:black").CommandLine.Should().Be("hatch run black .");
    }

    [Fact]
    public async Task Unittest_is_used_when_tests_exist_without_pytest()
    {
        using var fixture = new DetectionFixture()
            .With("setup.py", "from setuptools import setup\nsetup(name='lib', install_requires=['requests>=2'])\n")
            .With("tests/test_lib.py", "import unittest\n");

        var profile = await fixture.DetectAsync();

        profile.Command("python:unittest").Should().Match<DetectedCommand>(c => c.CommandLine == "python -m unittest discover" && c.Category == CommandCategory.Test);
        profile.Command("python:install").CommandLine.Should().Be("python -m pip install -e .");
        profile.Tests.Frameworks.Should().Contain("unittest");
        profile.HasCommand("python:pytest").Should().BeFalse();
    }

    [Theory]
    [InlineData("Django>=4.2", "django")]
    [InlineData("typing_extensions ; python_version < '3.11'", "typing-extensions")]
    [InlineData("zope.interface[docs]==6", "zope-interface")]
    [InlineData("  ruff", "ruff")]
    public void Requirement_names_are_normalized(string requirement, string expected) =>
        PythonDetector.RequirementName(requirement).Should().Be(expected);

    [Fact]
    public void Requirement_files_skip_options_urls_and_comments()
    {
        var names = PythonDetector.RequirementNames("# comment\n-e .\n--index-url https://x\nhttps://example.com/pkg.whl\nrequests  # http\n\n").ToList();

        names.Should().Equal("requests");
    }
}
