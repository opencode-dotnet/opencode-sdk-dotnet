using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Hosting;

public sealed class BatchCommandLineTests
{
    private const string Script = @"C:\shims\opencode.cmd";

    private static readonly string[] LauncherArguments = ["--stdio", "--port", "0"];

    [Test]
    public async Task Compose_Should_Wrap_The_Whole_Line_In_The_Pair_Slash_S_Strips()
    {
        var line = BatchCommandLine.Compose(Script, ["serve"], LauncherArguments);

        // cmd.exe /s drops exactly the first and last quote after /c, leaving each inner token
        // quoted: without the outer pair the script path and the first argument would merge.
        await Assert.That(line).IsEqualTo(
            @"/d /s /c """"C:\shims\opencode.cmd"" ""serve"" ""--stdio"" ""--port"" ""0""""");
    }

    [Test]
    public async Task Compose_Should_Quote_A_Script_Path_Holding_Spaces()
    {
        var line = BatchCommandLine.Compose(@"C:\Program Files\opencode.cmd", [], []);

        await Assert.That(line).IsEqualTo(@"/d /s /c """"C:\Program Files\opencode.cmd""""");
    }

    [Test]
    [Arguments("serve&calc")]
    [Arguments("serve|calc")]
    [Arguments("serve<in")]
    [Arguments("serve>out")]
    [Arguments("serve^x")]
    [Arguments("%PATH%")]
    [Arguments("serve!x!")]
    [Arguments("say \"hi\"")]
    [Arguments("serve\rcalc")]
    [Arguments("serve\ncalc")]
    public async Task Compose_Should_Refuse_A_Supplied_Argument_Carrying_A_Cmd_Metacharacter(string argument)
    {
        var refusal = await Assert.That(
            () => BatchCommandLine.Compose(Script, [argument], LauncherArguments))
            .Throws<OpenCodeServerException>();

        await Assert.That(refusal!.Message).Contains(argument);
        await Assert.That(refusal.Message).Contains("cmd.exe");
        await Assert.That(refusal.Message).Contains(Script);
    }

    [Test]
    public async Task Compose_Should_Accept_The_Arguments_The_Launcher_Appends_Itself()
    {
        var line = BatchCommandLine.Compose(Script, [], LauncherArguments);

        await Assert.That(line).Contains(@"""--stdio"" ""--port"" ""0""");
    }
}
