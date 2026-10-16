using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Tests;

public sealed class PinnedServerCommandOverrideTests
{
    private const string Variable = "OPENCODE_SDK_TESTS_SERVER_COMMAND";

    [Test]
    public async Task FromEnvironment_Should_Return_Null_When_The_Variable_Is_Unset()
    {
        var command = PinnedServerCommandOverride.FromEnvironment(static _ => null);

        await Assert.That(command).IsNull();
    }

    [Test]
    public async Task FromEnvironment_Should_Split_The_Command_On_Its_Separator()
    {
        var command = PinnedServerCommandOverride.FromEnvironment(Read("opencode|serve"));

        await Assert.That(command).IsNotNull();
        await Assert.That(command!.Command).IsEquivalentTo(["opencode", "serve"]);
    }

    /// <summary>
    /// The executable is handed to the launcher as written: a bare name is resolved from
    /// <c>PATH</c> there, which is the whole point of the variable, so nothing here probes it.
    /// </summary>
    [Test]
    public async Task FromEnvironment_Should_Keep_A_Relative_Executable_As_Written()
    {
        var command = PinnedServerCommandOverride.FromEnvironment(Read("opencode"));

        await Assert.That(command!.Command).IsEquivalentTo(["opencode"]);
    }

    /// <summary>The separator is what lets an absolute path with spaces stay one token.</summary>
    [Test]
    public async Task FromEnvironment_Should_Keep_A_Path_With_Spaces_As_One_Token()
    {
        var command = PinnedServerCommandOverride.FromEnvironment(Read(@"C:\Program Files\oc\opencode.cmd|serve"));

        await Assert.That(command!.Command).IsEquivalentTo([@"C:\Program Files\oc\opencode.cmd", "serve"]);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("|")]
    [Arguments("||")]
    public async Task FromEnvironment_Should_Throw_Naming_The_Variable_When_It_Carries_No_Token(string value)
    {
        var exception = await Assert
            .That(() => PinnedServerCommandOverride.FromEnvironment(Read(value)))
            .Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains(Variable);
    }

    [Test]
    public async Task ToString_Should_Render_The_Command_The_Way_A_Shell_Shows_It()
    {
        var command = PinnedServerCommandOverride.FromEnvironment(Read("opencode|serve"));

        await Assert.That(command!.ToString()).IsEqualTo("opencode serve");
    }

    /// <summary>Reads the one variable this type resolves, and nothing else.</summary>
    private static Func<string, string?> Read(string value) =>
        name => string.Equals(name, Variable, StringComparison.Ordinal) ? value : null;
}
