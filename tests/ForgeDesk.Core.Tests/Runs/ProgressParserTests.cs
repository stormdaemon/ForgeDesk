using ForgeDesk.Core.Runs;

namespace ForgeDesk.Core.Tests.Runs;

public class ProgressParserTests
{
    [Theory]
    [InlineData("[ 45%] Building CXX object src/CMakeFiles/app.dir/main.cpp.o", 0.45)] // CMake
    [InlineData("tests/test_api.py ....F.....                             [ 45%]", 0.45)] // pytest
    [InlineData("[100%] Linking CXX executable app", 1.0)]
    [InlineData("Progress: 45 %", 0.45)]
    [InlineData("progress=12.5%", 0.125)]
    [InlineData("[12/40] Compiling C object foo.o", 0.3)] // ninja
    [InlineData("#8 [3/6] RUN npm ci", 0.5)] // Docker BuildKit
    [InlineData("Step 3/7 : RUN dotnet restore", 3.0 / 7)] // Docker classic
    [InlineData("Resolving dependencies (3 of 10)", 0.3)]
    [InlineData("   Building [=======>             ] 45/100: serde_json", 0.45)] // Cargo
    [InlineData("Receiving objects:  45% (450/1000), 1.20 MiB | 2.40 MiB/s", 0.45)] // git
    [InlineData("<=========----> 70% EXECUTING [5s]", 0.7)] // Gradle
    [InlineData("10% building 0/1 entries 0/0 dependencies 0/0 modules", 0.1)] // webpack
    [InlineData(" 33%|███▎      | 33/100 [00:01<00:02, 30.00it/s]", 0.33)] // tqdm
    public void Recognizes_progress_indicators(string line, double expected)
    {
        ProgressParser.Parse(line).Should().BeApproximately(expected, 0.0001);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Compiling foo v0.1.0 (/src/foo)")]
    [InlineData("added 120 packages in 3s")]
    [InlineData("100% of tests passed")]
    [InlineData("Coverage: 85%")]
    [InlineData("Statements   : 85.71% ( 12/14 )")]
    [InlineData("CPU usage 45%")]
    [InlineData("Bundle size is 12% smaller")]
    [InlineData("| 85.5% | 70.1% | 90% | 88% |")]
    [InlineData("[2024/05] release notes")]
    [InlineData("Step 9/7 invalid")]
    [InlineData("[0/0] nothing")]
    [InlineData("150% done")]
    [InlineData("12:30:15 server listening on port 5173")]
    public void Ignores_lines_that_are_not_progress(string line)
    {
        ProgressParser.Parse(line).Should().BeNull();
    }

    [Fact]
    public void Tracker_moves_forward_and_ignores_small_regressions()
    {
        var tracker = new ProgressTracker();

        tracker.Observe("[ 10%] a").Should().BeTrue();
        tracker.Observe("[ 50%] b").Should().BeTrue();
        tracker.Observe("[ 40%] parallel job reports less").Should().BeFalse();
        tracker.Current.Should().BeApproximately(0.5, 0.0001);
        tracker.Observe("no progress here").Should().BeFalse();
        tracker.Observe("[ 50%] same").Should().BeFalse();
        tracker.Observe("[ 80%] c").Should().BeTrue();
        tracker.Current.Should().BeApproximately(0.8, 0.0001);
    }

    [Fact]
    public void Tracker_restarts_for_a_new_phase()
    {
        var tracker = new ProgressTracker();
        tracker.Update(1.0);
        tracker.Update(0.05).Should().BeTrue("the previous phase completed");
        tracker.Current.Should().BeApproximately(0.05, 0.0001);

        tracker.Update(0.9);
        tracker.Update(0.2).Should().BeTrue("a drop by more than half is a new phase");
    }

    [Fact]
    public void Tracker_is_null_until_something_is_recognized()
    {
        var tracker = new ProgressTracker();
        tracker.Observe("hello").Should().BeFalse();
        tracker.Current.Should().BeNull();
    }
}
