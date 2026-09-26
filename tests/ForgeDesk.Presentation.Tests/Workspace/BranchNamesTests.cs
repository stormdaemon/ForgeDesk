using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Tests.Workspace;

public class BranchNamesTests
{
    [Theory]
    [InlineData("feature/login")]
    [InlineData("fix-123")]
    [InlineData("release/2.0.1")]
    [InlineData("user/jane/experiment_1")]
    public void Valid_names_are_accepted(string name) => BranchNames.Validate(name).Should().BeNull();

    [Theory]
    [InlineData("", "Enter a branch name.")]
    [InlineData("   ", "Enter a branch name.")]
    [InlineData("my branch", "Branch names can't contain spaces. Use '-' or '/' instead.")]
    [InlineData("-start", "Branch names can't start with '-'.")]
    [InlineData("a..b", "Branch names can't contain '..'.")]
    [InlineData("what?", "Branch names can't contain ~ ^ : ? * [ or \\.")]
    [InlineData("back\\slash", "Branch names can't contain ~ ^ : ? * [ or \\.")]
    [InlineData("@", "Branch names can't be '@' or contain '@{'.")]
    [InlineData("a@{1}", "Branch names can't be '@' or contain '@{'.")]
    [InlineData("/lead", "Branch names can't start or end with '/' or contain '//'.")]
    [InlineData("trail/", "Branch names can't start or end with '/' or contain '//'.")]
    [InlineData("a//b", "Branch names can't start or end with '/' or contain '//'.")]
    [InlineData("name.", "Branch names can't end with '.' or '.lock'.")]
    [InlineData("name.lock", "Branch names can't end with '.' or '.lock'.")]
    [InlineData("feature/.hidden", "No part of a branch name can start with '.'.")]
    public void Invalid_names_explain_why(string name, string message) => BranchNames.Validate(name).Should().Be(message);

    [Fact]
    public void Existing_names_are_rejected_ignoring_case() =>
        BranchNames.Validate("Main", ["main", "dev"]).Should().Be("A branch named 'Main' already exists.");
}
