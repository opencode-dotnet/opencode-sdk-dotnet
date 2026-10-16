namespace OpenCode.Sdk.Tools.Tests.Support;

public sealed class ScriptedProcessRunnerTests
{
    [Test]
    [Arguments("git", "show", "path with spaces", "extra")]
    [Arguments("git", "show", "path", "with spaces")]
    [Arguments("git", "path with spaces", "show", "")]
    [Arguments("other", "show", "path with spaces", "")]
    [Arguments("git", "show", "", "")]
    public async Task RunAsync_Should_Reject_A_Different_Invocation(string executable, string first, string second, string third)
    {
        var runner = new ScriptedProcessRunner().Expect("git", ["show", "path with spaces"], "repository");
        var arguments = new[] { first, second, third }.Where(static token => token.Length > 0).ToArray();

        await Assert.That(async () => _ = await runner.RunAsync(executable, arguments, "repository", CancellationToken.None))
            .Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments("repository", false)]
    [Arguments("elsewhere", true)]
    public async Task RunAsync_Should_Check_The_Working_Directory(string directory, bool refuses)
    {
        var runner = new ScriptedProcessRunner().Expect("git", ["show", "path with spaces"], "repository");
        if (refuses)
        {
            await Assert.That(async () => _ = await runner.RunAsync("git", ["show", "path with spaces"], directory, CancellationToken.None))
                .Throws<InvalidOperationException>();
        }
        else
        {
            await Assert.That((await runner.RunAsync("git", ["show", "path with spaces"], directory, CancellationToken.None)).ExitCode).IsEqualTo(0);
            await Assert.That(async () => _ = await runner.RunAsync("git", [], directory, CancellationToken.None))
                .Throws<InvalidOperationException>();
        }
    }
}
