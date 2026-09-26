using ForgeDesk.Core.Runs;

namespace ForgeDesk.Core.Tests.Runs;

public class ErrorSummaryExtractorTests
{
    private static string? Extract(string output, bool failed = true) =>
        ErrorSummaryExtractor.Extract(output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'), failed);

    [Fact]
    public void Dotnet_build_errors_are_collected_once()
    {
        const string output = """
            Determining projects to restore...
            All projects are up-to-date for restore.
            /src/App/Program.cs(3,10): error CS1002: ; expected [/src/App/App.csproj]
            /src/App/Program.cs(8,1): error CS0103: The name 'foo' does not exist in the current context [/src/App/App.csproj]

            Build FAILED.

            /src/App/Program.cs(3,10): error CS1002: ; expected [/src/App/App.csproj]
            /src/App/Program.cs(8,1): error CS0103: The name 'foo' does not exist in the current context [/src/App/App.csproj]
                0 Warning(s)
                2 Error(s)
            """;

        var summary = Extract(output)!;

        summary.Should().Contain("error CS1002").And.Contain("error CS0103").And.Contain("Build FAILED.");
        summary.Split('\n').Count(l => l.Contains("CS1002", StringComparison.Ordinal)).Should().Be(1);
        summary.Should().NotContain("0 Warning(s)");
    }

    [Fact]
    public void Typescript_errors_are_collected()
    {
        const string output = """
            > tsc --noEmit

            src/index.ts(12,7): error TS2322: Type 'string' is not assignable to type 'number'.
            src/api.ts:3:10 - error TS2345: Argument of type 'undefined' is not assignable.

            Found 2 errors in 2 files.
            """;

        var summary = Extract(output)!;
        summary.Should().Contain("error TS2322").And.Contain("error TS2345");
    }

    [Fact]
    public void Npm_failures_keep_the_script_error_and_drop_boilerplate()
    {
        const string output = """
            > app@1.0.0 build
            > node build.js

            Error: Cannot find module 'left-pad'
                at Function.Module._resolveFilename (node:internal/modules/cjs/loader:1039:15)
            npm ERR! code 1
            npm ERR! path /src/app
            npm ERR! A complete log of this run can be found in:
            npm ERR!     /home/me/.npm/_logs/2026-09-25-debug-0.log
            """;

        var summary = Extract(output)!;
        summary.Should().Contain("Error: Cannot find module 'left-pad'");
        summary.Should().Contain("npm ERR! code 1");
        summary.Should().NotContain("A complete log of this run");
    }

    [Fact]
    public void Cargo_errors_and_panics_are_collected()
    {
        const string output = """
               Compiling demo v0.1.0 (/src/demo)
            error[E0425]: cannot find value `x` in this scope
             --> src/main.rs:2:13
              |
            2 |     let y = x + 1;
              |             ^ not found in this scope

            error: could not compile `demo` (bin "demo") due to 1 previous error
            """;

        var summary = Extract(output)!;
        summary.Should().Contain("error[E0425]: cannot find value `x` in this scope");
        summary.Should().Contain("--> src/main.rs:2:13");
        summary.Should().Contain("error: could not compile");

        var panic = Extract("""
            running 1 test
            thread 'tests::it_works' panicked at src/lib.rs:10:9:
            assertion `left == right` failed
            test result: FAILED. 0 passed; 1 failed; 0 ignored
            """)!;
        panic.Should().Contain("panicked at src/lib.rs:10:9").And.Contain("test result: FAILED.");
    }

    [Fact]
    public void Pytest_failures_are_collected()
    {
        const string output = """
            ============================= test session starts ==============================
            collected 3 items

            tests/test_math.py .F.                                                   [100%]

            =================================== FAILURES ===================================
            _________________________________ test_divide __________________________________

                def test_divide():
            >       assert divide(4, 2) == 3
            E       assert 2.0 == 3
            E        +  where 2.0 = divide(4, 2)

            tests/test_math.py:8: AssertionError
            =========================== short test summary info ============================
            FAILED tests/test_math.py::test_divide - assert 2.0 == 3
            ========================= 1 failed, 2 passed in 0.05s ==========================
            """;

        var summary = Extract(output)!;
        summary.Should().Contain("E       assert 2.0 == 3");
        summary.Should().Contain("FAILED tests/test_math.py::test_divide");
        summary.Should().Contain("AssertionError");
    }

    [Fact]
    public void Python_tracebacks_are_collected()
    {
        const string output = """
            Starting job
            Traceback (most recent call last):
              File "main.py", line 3, in <module>
                run()
              File "main.py", line 2, in run
                raise ValueError("bad input")
            ValueError: bad input
            """;

        var summary = Extract(output)!;
        summary.Should().Contain("Traceback (most recent call last):");
        summary.Should().Contain("ValueError: bad input");
    }

    [Fact]
    public void Gradle_and_maven_failures_are_collected()
    {
        var gradle = Extract("""
            > Task :app:compileJava FAILED
            /src/app/Main.java:3: error: ';' expected
                    System.out.println("hi")
                                            ^
            FAILURE: Build failed with an exception.
            * What went wrong:
            Execution failed for task ':app:compileJava'.
            BUILD FAILED in 2s
            """)!;
        gradle.Should().Contain("error: ';' expected").And.Contain("Execution failed for task ':app:compileJava'.").And.Contain("BUILD FAILED in 2s");

        var maven = Extract("""
            [INFO] Compiling 3 source files
            [ERROR] COMPILATION ERROR :
            [ERROR] /src/Main.java:[3,27] ';' expected
            [INFO] BUILD FAILURE
            """)!;
        maven.Should().Contain("[ERROR] /src/Main.java:[3,27] ';' expected").And.Contain("BUILD FAILURE");
    }

    [Fact]
    public void Git_fatal_errors_and_test_crosses_are_collected()
    {
        Extract("fatal: not a git repository (or any of the parent directories): .git")
            .Should().Contain("fatal: not a git repository");

        Extract("""
             ✓ adds numbers
             ✗ divides numbers
               expected 2 to equal 3
            """).Should().Contain("✗ divides numbers").And.Contain("expected 2 to equal 3");
    }

    [Fact]
    public void Summary_is_bounded()
    {
        var lines = Enumerable.Range(0, 5_000).Select(i => $"src/file{i}.c:1:1: error: something went wrong number {i} {new string('x', 100)}");
        var summary = ErrorSummaryExtractor.Extract(lines, failed: true)!;

        summary.Split('\n').Length.Should().BeLessThanOrEqualTo(ErrorSummaryExtractor.MaxLines);
        summary.Length.Should().BeLessThanOrEqualTo(ErrorSummaryExtractor.MaxChars);
        summary.Should().StartWith("src/file0.c");
    }

    [Fact]
    public void Very_long_lines_are_shortened()
    {
        var summary = Extract("error: " + new string('y', 5_000))!;
        summary.Length.Should().BeLessThan(500);
        summary.Should().EndWith("…");
    }

    [Fact]
    public void Falls_back_to_the_last_lines_when_nothing_is_recognized()
    {
        var output = string.Join('\n', Enumerable.Range(1, 40).Select(i => $"step {i}"));
        var summary = Extract(output)!;

        var lines = summary.Split('\n');
        lines.Should().HaveCount(ErrorSummaryExtractor.FallbackLines);
        lines[0].Should().Be("step 26");
        lines[^1].Should().Be("step 40");
    }

    [Fact]
    public void Successful_runs_have_no_summary()
    {
        Extract("error: this was only a warning in disguise", failed: false).Should().BeNull();
    }

    [Fact]
    public void Failed_run_without_output_has_no_summary()
    {
        ErrorSummaryExtractor.Extract([], failed: true).Should().BeNull();
    }

    [Theory]
    [InlineData("    0 Error(s)")]
    [InlineData("Tests: 0 failed, 12 passed")]
    [InlineData("Failed: 0, Passed: 12")]
    [InlineData("Compiled with no errors")]
    [InlineData("Loading error-handling.ts")]
    [InlineData("gcc -Werror -o app main.c")]
    public void Success_counters_are_not_errors(string line)
    {
        ErrorSummaryExtractor.IsErrorLine(line).Should().BeFalse();
    }

    [Theory]
    [InlineData("error: linking with `cc` failed")]
    [InlineData("ERROR in ./src/index.js 3:0-24")]
    [InlineData("npm error code ELIFECYCLE")]
    [InlineData("TypeError: Cannot read properties of undefined (reading 'map')")]
    [InlineData("Exception in thread \"main\" java.lang.NullPointerException")]
    [InlineData("FAIL src/app.test.ts")]
    [InlineData("C:\\src\\App.csproj : error NU1101: Unable to find package Foo.")]
    [InlineData("LINK : fatal error LNK1181: cannot open input file 'foo.lib'")]
    public void Recognizes_error_lines(string line)
    {
        ErrorSummaryExtractor.IsErrorLine(line).Should().BeTrue();
    }
}
